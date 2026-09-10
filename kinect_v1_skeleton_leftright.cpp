// kinect_v1_skeleton_leftright.cpp
//
// Recupere le flux squelette de la Kinect v1 (modele 1414) via le SDK
// Kinect for Windows v1.8, et affiche dans quelle zone (gauche, milieu,
// droite) se trouve le joueur en fonction de la position X absolue du
// joint hip_center, ainsi que sa posture (debout, saut, accroupi) en
// fonction de la position Y de ce meme joint par rapport a une position
// de reference calibree au moment du verrouillage du squelette.
//
// Un flux video couleur peut etre active/desactive a tout moment en
// appuyant sur la touche 'V' (variable booleenne g_videoStreamEnabled).
// L'affichage se fait dans une fenetre Win32 native, dessinee avec GDI
// (StretchDIBits) -- aucune dependance externe type OpenCV. Le squelette
// detecte (os + articulations) et un texte d'etat (zone/posture) sont
// dessines en surimpression sur ce flux video.
//
// Le programme se connecte en TCP (127.0.0.1:5000, non bloquant, avec
// reconnexion automatique) a un serveur Python (pynput) qui traduit des
// commandes texte ("haut", "bas", "gauche", "droite", "start_down",
// "start_up"...) en appuis clavier reels. On n'envoie PAS l'etat absolu
// (zone/posture) mais uniquement le mouvement correspondant a la
// TRANSITION d'etat :
//   - gauche -> milieu ou milieu -> droite  => "droite"
//   - droite -> milieu ou milieu -> gauche  => "gauche"
//   - (peu importe l'etat de depart) -> saut     => "haut"
//   - (peu importe l'etat de depart) -> accroupi => "bas"
//   - -> debout : rien n'est envoye (position neutre = pas d'action)
//
// De plus, un sejour continu en zone MILIEU declenche un maintien reel
// de la touche 'S' (key down / key up separes, pas un simple clic) :
// apres 3 secondes en MILIEU, 'S' est enfoncee ("start_down"), puis
// relachee automatiquement 4 secondes plus tard ("start_up"), ou plus
// tot si le joueur quitte la zone MILIEU entre-temps.
//
// Setup du projet Visual Studio :
//   - Includes : $(KINECTSDK10_DIR)inc
//   - Lib dirs : $(KINECTSDK10_DIR)lib\x86  (ou \x64 selon la config)
//   - Linker > Input > Additional Dependencies :
//         Kinect10.lib Gdi32.lib User32.lib Ws2_32.lib
//   ($(KINECTSDK10_DIR) est une variable d'environnement definie
//    automatiquement par l'installeur du SDK)
//
// Compilation en ligne de commande (cl.exe) :
//   cl /EHsc kinect_v1_skeleton_leftright.cpp /I "%KINECTSDK10_DIR%inc" ^
//      /link /LIBPATH:"%KINECTSDK10_DIR%lib\x86" ^
//      Kinect10.lib Gdi32.lib User32.lib Ws2_32.lib
//
// Cote Python, lancer le serveur AVANT (ou apres, peu importe grace a la
// reconnexion automatique) le programme C++ :
//   python kinect_keyboard_server.py

// IMPORTANT : winsock2.h doit etre inclus avant windows.h (ou
// WIN32_LEAN_AND_MEAN doit etre defini avant) pour eviter les conflits
// avec l'ancien winsock.h inclus automatiquement par windows.h.
//
// IMPORTANT (2) : WIN32_LEAN_AND_MEAN empeche windows.h d'inclure les
// en-tetes COM (ole2.h -> objbase.h -> unknwn.h) qui definissent la
// macro `interface` (#define interface struct). NuiSensor.h utilise
// cette macro pour declarer ses interfaces COM (INuiSensor, etc.).
// Sans elle, la compilation echoue en cascade (C4430/C2146/C2371).
// On inclut donc explicitement <unknwn.h> avant <NuiApi.h>.
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <unknwn.h>   // definit la macro `interface` (COM), requis par NuiSensor.h
#include <NuiApi.h>
#include <iostream>
#include <string>
#include <vector>
#include <cstdio>

#pragma comment(lib, "Ws2_32.lib")

static HANDLE      g_hNextSkeletonEvent = NULL;
static INuiSensor* g_pNuiSensor         = NULL;

// --- Squelette ---------------------------------------------------------

// TrackingID du squelette verrouille. 0 = aucun squelette verrouille
// pour l'instant (on prendra le premier detecte).
static DWORD g_lockedTrackingID = 0;

// Bornes des 3 zones, en metres, sur l'axe X du capteur
// (X negatif = gauche du capteur, X positif = droite du capteur).
// A ajuster selon la largeur de la zone de jeu voulue.
static const float ZONE_LEFT_LIMIT  = -0.2f; // en dessous -> GAUCHE
static const float ZONE_RIGHT_LIMIT =  0.2f; // au dessus  -> DROITE
                                              // entre les deux -> MILIEU

// L'ordre des valeurs compte : il est utilise pour calculer le nombre
// et le sens des mouvements clavier lors d'un changement de zone (voir
// SendZoneMovement plus bas). GAUCHE < MILIEU < DROITE.
enum class Zone { GAUCHE = 0, MILIEU = 1, DROITE = 2 };

static Zone GetZoneFromX(float x)
{
    if (x < ZONE_LEFT_LIMIT)  return Zone::GAUCHE;
    if (x > ZONE_RIGHT_LIMIT) return Zone::DROITE;
    return Zone::MILIEU;
}

static const char* ZoneToString(Zone z)
{
    switch (z)
    {
        case Zone::GAUCHE: return "GAUCHE";
        case Zone::DROITE: return "DROITE";
        default:           return "MILIEU";
    }
}

// --- Detection saut / accroupi (posture) --------------------------------
//
// On compare la position Y du hip_center a une position de reference
// (g_baselineHipY), calibree automatiquement sur les premieres frames
// suivant le verrouillage d'un nouveau squelette (le joueur est suppose
// se tenir debout, en position neutre, a ce moment-la).
//
// Delta positif -> le joueur est plus haut que la reference -> SAUT
// Delta negatif -> le joueur est plus bas que la reference   -> ACCROUPI

enum class Posture { ACCROUPI, DEBOUT, SAUT };

// Seuils de declenchement, en metres, par rapport a la reference.
static const float JUMP_Y_THRESHOLD    =  0.10f; // au-dessus -> SAUT
static const float CROUCH_Y_THRESHOLD  = -0.15f; // en dessous -> ACCROUPI

// Nombre de frames utilisees pour etablir la position de reference au
// moment du verrouillage (moyenne glissante simple).
static const int CALIBRATION_FRAME_COUNT = 15;

static bool  g_hasBaselineY        = false;
static float g_baselineHipY        = 0.0f;
static int   g_calibrationSamples  = 0;
static float g_calibrationSum      = 0.0f;

// Passe a true pour afficher le delta Y brut (y - reference) a CHAQUE
// frame, meme sans franchissement de seuil. Tres utile pour calibrer
// JUMP_Y_THRESHOLD / CROUCH_Y_THRESHOLD a la main en observant les
// valeurs reelles produites par un saut ou un accroupissement.
static const bool DEBUG_POSTURE_DELTA = false;

// Un squelette en accroupissement (ou partiellement hors cadre) bascule
// souvent de NUI_SKELETON_TRACKED vers NUI_SKELETON_POSITION_ONLY : les
// joints individuels ne sont plus calcules, mais la position globale
// (donc le hip_center) reste valide. Si on exigeait NUI_SKELETON_TRACKED
// strictement, on perdait le verrou -> reinitialisation de la reference
// Y au moment meme ou le joueur s'accroupit, ce qui annulait totalement
// la detection (la reference "collait" toujours a la posture courante).
static bool IsSkeletonStateUsable(NUI_SKELETON_TRACKING_STATE state)
{
    return state == NUI_SKELETON_TRACKED || state == NUI_SKELETON_POSITION_ONLY;
}

// --- Filtrage par profondeur (distance au capteur) ----------------------
//
// Ignore tout squelette (verrouillage ou nouvelle detection) dont la
// distance au capteur (composante Z du hip_center, en metres) depasse ce
// seuil. Utile pour exclure une personne trop loin du capteur (fond de
// piece, couloir visible derriere, etc.). Le champ de profondeur fiable
// de la Kinect v1 va jusqu'a environ 4-4.5 m ; au-dela les donnees sont
// de toute facon peu exploitables.
static const float MAX_DETECTION_DEPTH_METERS = 3.0f;

// Verifie que le hip_center du squelette est a une distance exploitable :
// Z > 0 (position valide) et <= au seuil configure ci-dessus.
static bool IsSkeletonWithinDepthRange(const NUI_SKELETON_DATA& skeleton)
{
    float z = skeleton.SkeletonPositions[NUI_SKELETON_POSITION_HIP_CENTER].z;
    return z > 0.0f && z <= MAX_DETECTION_DEPTH_METERS;
}

static Posture GetPostureFromY(float currentY, float baselineY)
{
    float delta = currentY - baselineY;
    if (delta > JUMP_Y_THRESHOLD)   return Posture::SAUT;
    if (delta < CROUCH_Y_THRESHOLD) return Posture::ACCROUPI;
    return Posture::DEBOUT;
}

static const char* PostureToString(Posture p)
{
    switch (p)
    {
        case Posture::SAUT:     return "SAUT";
        case Posture::ACCROUPI: return "ACCROUPI";
        default:                return "DEBOUT";
    }
}

// Reinitialise la calibration de la reference Y ; a appeler a chaque
// fois qu'un nouveau squelette est verrouille.
static void ResetYCalibration()
{
    g_hasBaselineY       = false;
    g_calibrationSamples = 0;
    g_calibrationSum     = 0.0f;
}

// --- Detection "maintien au milieu" (signal start maintenu) --------------
//
// Si le joueur reste en continu dans la zone MILIEU pendant au moins
// MIDDLE_HOLD_BEFORE_PRESS_MS, on ENFONCE la touche 'S' (start_down,
// key down reel, pas un simple clic) et on la maintient MIDDLE_HOLD_
// DURATION_MS de plus avant de la relacher automatiquement (start_up).
// Si le joueur quitte la zone MILIEU pendant que la touche est
// enfoncee, elle est relachee immediatement pour ne jamais rester
// bloquee. Aucun appui n'est envoye si le joueur quitte MILIEU avant
// le declenchement initial (avant les 3 premieres secondes).
static const DWORD MIDDLE_HOLD_BEFORE_PRESS_MS   = 100;  // attente avant le press
static const DWORD MIDDLE_HOLD_DURATION_MS       = 4000; // duree du maintien apres le press
static const DWORD MIDDLE_HOLD_RETRIGGER_DELAY_MS = 100; // pause apres le relachement avant de reprendre la detection

enum class MiddleHoldState { IDLE, WAITING, HOLDING, RELEASED };
// --- Communication reseau avec le serveur clavier (Python) --------------
//
// Le script Python (pynput) ecoute en TCP sur 127.0.0.1:5000 et attend
// des commandes texte terminees par '\n' (ex: "gauche\n", "haut\n").
// La plupart des commandes recues declenchent une pression + relachement
// de la touche clavier correspondante (voir le dictionnaire KEYS du
// script). Deux commandes speciales, "start_down" et "start_up", font
// respectivement un press et un release explicites et separes de la
// touche 'S', pour un vrai maintien plutot qu'un simple clic.
//
// La connexion est geree en mode NON BLOQUANT : si le serveur Python
// n'est pas encore lance, ou se ferme/plante en cours de route, la
// boucle principale (lecture Kinect + fenetre video) n'est jamais
// stoppee. Une tentative de reconnexion est retentee automatiquement
// toutes les NETWORK_RETRY_MS millisecondes.

static const char*   NETWORK_HOST     = "127.0.0.1";
static const u_short  NETWORK_PORT     = 5000;
static const DWORD   NETWORK_RETRY_MS = 3000;

static SOCKET g_keyboardSocket      = INVALID_SOCKET;
static bool   g_networkConnected    = false;
static DWORD  g_lastConnectAttempt  = 0;

static bool InitWinsock()
{
    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
    {
        std::cerr << "WSAStartup a echoue.\n";
        return false;
    }
    return true;
}

// Lance (ou relance) une connexion non bloquante vers le serveur Python.
// Ne bloque jamais : connect() retourne immediatement sur un socket non
// bloquant, et la reussite/echec reel est verifie plus tard par
// PollKeyboardConnection() via select().
static void TryConnectKeyboardServer()
{
    DWORD now = GetTickCount();
    if (g_lastConnectAttempt != 0 && (now - g_lastConnectAttempt) < NETWORK_RETRY_MS)
        return; // trop tot pour retenter

    g_lastConnectAttempt = now;

    if (g_keyboardSocket != INVALID_SOCKET)
    {
        closesocket(g_keyboardSocket);
        g_keyboardSocket = INVALID_SOCKET;
    }

    g_keyboardSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (g_keyboardSocket == INVALID_SOCKET)
        return;

    u_long nonBlocking = 1;
    ioctlsocket(g_keyboardSocket, FIONBIO, &nonBlocking);

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port   = htons(NETWORK_PORT);
    inet_pton(AF_INET, NETWORK_HOST, &addr.sin_addr);

    connect(g_keyboardSocket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));
    // Resultat reel ignore ici : verifie par PollKeyboardConnection().
}

// A appeler a chaque tour de boucle principale. Si une connexion est en
// cours d'etablissement, verifie si elle vient d'aboutir (socket devenu
// inscriptible = connecte) ou a echoue. Si on est deconnecte depuis
// assez longtemps, relance une tentative.
static void PollKeyboardConnection()
{
    if (g_networkConnected)
        return;

    if (g_keyboardSocket == INVALID_SOCKET)
    {
        TryConnectKeyboardServer();
        return;
    }

    fd_set writeSet;
    FD_ZERO(&writeSet);
    FD_SET(g_keyboardSocket, &writeSet);

    fd_set errorSet;
    FD_ZERO(&errorSet);
    FD_SET(g_keyboardSocket, &errorSet);

    TIMEVAL timeout = {0, 0}; // interrogation instantanee, jamais bloquant
    int result = select(0, NULL, &writeSet, &errorSet, &timeout);

    if (result > 0)
    {
        if (FD_ISSET(g_keyboardSocket, &errorSet))
        {
            closesocket(g_keyboardSocket);
            g_keyboardSocket = INVALID_SOCKET;
        }
        else if (FD_ISSET(g_keyboardSocket, &writeSet))
        {
            g_networkConnected = true;
            std::cout << "Connecte au serveur clavier (" << NETWORK_HOST
                       << ":" << NETWORK_PORT << ")\n";
        }
    }
    else
    {
        // Toujours en cours de connexion : on retentera au prochain
        // depassement de NETWORK_RETRY_MS si necessaire.
        TryConnectKeyboardServer();
    }
}

// Envoie une commande texte (ex: "haut", "gauche", "start_down"...) au
// serveur Python. Ne fait rien si pas connecte. En cas d'echec d'envoi
// (serveur ferme la connexion), on repasse en mode "deconnecte" pour
// retenter plus tard via PollKeyboardConnection().
static void SendKeyCommand(const char* command)
{
    if (!g_networkConnected || g_keyboardSocket == INVALID_SOCKET)
        return;

    std::string line = std::string(command) + "\n";
    int sent = send(g_keyboardSocket, line.c_str(), static_cast<int>(line.size()), 0);

    if (sent == SOCKET_ERROR)
    {
        int err = WSAGetLastError();
        if (err != WSAEWOULDBLOCK)
        {
            std::cerr << "Connexion au serveur clavier perdue, "
                         "nouvelle tentative dans " << NETWORK_RETRY_MS << " ms\n";
            closesocket(g_keyboardSocket);
            g_keyboardSocket   = INVALID_SOCKET;
            g_networkConnected = false;
        }
    }
}

// Convertit un CHANGEMENT de zone en mouvement clavier gauche/droite.
// On ne connait/envoie jamais la zone absolue : seulement le sens du
// deplacement relatif, en fonction de l'ordre GAUCHE(0) < MILIEU(1) <
// DROITE(2). Ex : GAUCHE -> MILIEU ou MILIEU -> DROITE => "droite" ;
// DROITE -> MILIEU ou MILIEU -> GAUCHE => "gauche". Si jamais deux
// frames sautent une zone (GAUCHE -> DROITE directement), on envoie le
// mouvement plusieurs fois pour rattraper l'ecart.
static void SendZoneMovement(Zone from, Zone to)
{
    int diff = static_cast<int>(to) - static_cast<int>(from);

    while (diff > 0) { SendKeyCommand("droite"); --diff; }
    while (diff < 0) { SendKeyCommand("gauche"); ++diff; }
}

// Pour la posture, seuls SAUT et ACCROUPI declenchent un appui clavier.
// Revenir a DEBOUT ne fait rien : c'est la position neutre, "rien n'a
// besoin d'etre fait" comme demande.
static void SendPostureCommand(Posture p)
{
    switch (p)
    {
        case Posture::SAUT:     SendKeyCommand("haut"); break;
        case Posture::ACCROUPI: SendKeyCommand("bas");  break;
        case Posture::DEBOUT:
        default:
            break; // neutre : aucune commande envoyee
    }
}

static void CleanupNetwork()
{
    if (g_keyboardSocket != INVALID_SOCKET)
        closesocket(g_keyboardSocket);
    WSACleanup();
}

// --- Flux video couleur (fenetre Win32 + GDI natif) ---------------------

// Active/desactive l'ouverture et l'affichage du flux video couleur.
// Peut etre bascule a chaud (touche 'V' dans la boucle principale, ou en
// fermant la fenetre video avec la croix).
static bool g_videoStreamEnabled = false;

static const int  VIDEO_WIDTH  = 640;
static const int  VIDEO_HEIGHT = 480;
static const char* VIDEO_WINDOW_CLASS = "KinectVideoWindowClass";
static const char* VIDEO_WINDOW_TITLE = "Kinect - Flux couleur";

static HANDLE g_hNextColorFrameEvent = NULL;
static HANDLE g_hColorStreamHandle   = NULL;
static bool   g_colorStreamOpened    = false;

static HWND g_hVideoWindow = NULL;

// Dernier buffer BGRA recu de la Kinect, reaffiche a chaque WM_PAINT.
static std::vector<BYTE> g_videoBuffer(VIDEO_WIDTH * VIDEO_HEIGHT * 4, 0);

static BITMAPINFO g_bmi = {};

// --- Superposition squelette + infos sur le flux video ------------------
//
// A chaque frame squelette traitee (ProcessSkeletonFrame), on projette
// les articulations du joueur verrouille dans l'espace image couleur
// (640x480, meme resolution que la fenetre video) via les fonctions de
// mapping du SDK. Ces positions ecran sont simplement relues (jamais
// recalculees) au moment du dessin dans WM_PAINT.

// Paires de joints formant les "os" du squelette, pour tracer les
// segments reliant les articulations.
static const NUI_SKELETON_POSITION_INDEX SKELETON_BONES[][2] =
{
    { NUI_SKELETON_POSITION_HIP_CENTER,      NUI_SKELETON_POSITION_SPINE },
    { NUI_SKELETON_POSITION_SPINE,           NUI_SKELETON_POSITION_SHOULDER_CENTER },
    { NUI_SKELETON_POSITION_SHOULDER_CENTER, NUI_SKELETON_POSITION_HEAD },

    { NUI_SKELETON_POSITION_SHOULDER_CENTER, NUI_SKELETON_POSITION_SHOULDER_LEFT },
    { NUI_SKELETON_POSITION_SHOULDER_LEFT,   NUI_SKELETON_POSITION_ELBOW_LEFT },
    { NUI_SKELETON_POSITION_ELBOW_LEFT,      NUI_SKELETON_POSITION_WRIST_LEFT },
    { NUI_SKELETON_POSITION_WRIST_LEFT,      NUI_SKELETON_POSITION_HAND_LEFT },

    { NUI_SKELETON_POSITION_SHOULDER_CENTER, NUI_SKELETON_POSITION_SHOULDER_RIGHT },
    { NUI_SKELETON_POSITION_SHOULDER_RIGHT,  NUI_SKELETON_POSITION_ELBOW_RIGHT },
    { NUI_SKELETON_POSITION_ELBOW_RIGHT,     NUI_SKELETON_POSITION_WRIST_RIGHT },
    { NUI_SKELETON_POSITION_WRIST_RIGHT,     NUI_SKELETON_POSITION_HAND_RIGHT },

    { NUI_SKELETON_POSITION_HIP_CENTER,      NUI_SKELETON_POSITION_HIP_LEFT },
    { NUI_SKELETON_POSITION_HIP_LEFT,        NUI_SKELETON_POSITION_KNEE_LEFT },
    { NUI_SKELETON_POSITION_KNEE_LEFT,       NUI_SKELETON_POSITION_ANKLE_LEFT },
    { NUI_SKELETON_POSITION_ANKLE_LEFT,      NUI_SKELETON_POSITION_FOOT_LEFT },

    { NUI_SKELETON_POSITION_HIP_CENTER,      NUI_SKELETON_POSITION_HIP_RIGHT },
    { NUI_SKELETON_POSITION_HIP_RIGHT,       NUI_SKELETON_POSITION_KNEE_RIGHT },
    { NUI_SKELETON_POSITION_KNEE_RIGHT,      NUI_SKELETON_POSITION_ANKLE_RIGHT },
    { NUI_SKELETON_POSITION_ANKLE_RIGHT,     NUI_SKELETON_POSITION_FOOT_RIGHT },
};
static const int SKELETON_BONE_COUNT = sizeof(SKELETON_BONES) / sizeof(SKELETON_BONES[0]);

// Position ecran (repere de l'image couleur 640x480) de chaque
// articulation du joueur verrouille, et validite individuelle (un
// joint NOT_TRACKED n'est pas dessine).
static POINT g_jointScreenPos[NUI_SKELETON_POSITION_COUNT];
static bool  g_jointValid[NUI_SKELETON_POSITION_COUNT];
static bool  g_hasSkeletonOverlay = false; // un squelette est-il actuellement projete ?

// Texte d'etat (zone / posture) affiche en surimpression sur la video.
static char g_overlayStatusText[256] = "En attente d'un squelette...";

// Convertit une position squelette (metres, repere capteur) en pixel
// de l'image couleur 640x480. Passe par l'image depth comme etape
// intermediaire (requis par le SDK), mais aucun flux depth n'a besoin
// d'etre ouvert : c'est une transformation geometrique pure.
static bool MapSkeletonPointToScreen(const Vector4& skeletonPoint, POINT& outScreen)
{
    LONG   depthX = 0, depthY = 0;
    USHORT depthValue = 0;

    NuiTransformSkeletonToDepthImage(
        skeletonPoint,
        &depthX, &depthY, &depthValue,
        NUI_IMAGE_RESOLUTION_640x480);

    LONG colorX = 0, colorY = 0;
    HRESULT hr = NuiImageGetColorPixelCoordinatesFromDepthPixelAtResolution(
        NUI_IMAGE_RESOLUTION_640x480,  // resolution couleur (fenetre video)
        NUI_IMAGE_RESOLUTION_640x480,  // resolution depth utilisee ci-dessus
        NULL,                          // pas de zoom/pan numerique
        depthX, depthY, depthValue,
        &colorX, &colorY);

    if (FAILED(hr))
        return false;

    outScreen.x = colorX;
    outScreen.y = colorY;
    return true;
}

// Projette chaque articulation trackee du squelette verrouille ; a
// appeler a chaque frame squelette traitee (ProcessSkeletonFrame).
static void UpdateSkeletonOverlay(const NUI_SKELETON_DATA& skeleton)
{
    g_hasSkeletonOverlay = true;

    for (int i = 0; i < NUI_SKELETON_POSITION_COUNT; ++i)
    {
        if (skeleton.eSkeletonPositionTrackingState[i] == NUI_SKELETON_POSITION_NOT_TRACKED)
        {
            g_jointValid[i] = false;
            continue;
        }

        g_jointValid[i] = MapSkeletonPointToScreen(skeleton.SkeletonPositions[i], g_jointScreenPos[i]);
    }
}

// Dessine les os et les articulations du squelette verrouille par
// dessus l'image video deja blittee (appele depuis WM_PAINT).
static void DrawSkeletonOverlay(HDC hdc)
{
    if (!g_hasSkeletonOverlay)
        return;

    HPEN   bonePen    = CreatePen(PS_SOLID, 3, RGB(0, 255, 0));
    HPEN   oldPen     = (HPEN)SelectObject(hdc, bonePen);
    HBRUSH jointBrush = CreateSolidBrush(RGB(255, 0, 0));
    HBRUSH oldBrush   = (HBRUSH)SelectObject(hdc, jointBrush);

    for (int i = 0; i < SKELETON_BONE_COUNT; ++i)
    {
        int a = SKELETON_BONES[i][0];
        int b = SKELETON_BONES[i][1];

        if (!g_jointValid[a] || !g_jointValid[b])
            continue;

        MoveToEx(hdc, g_jointScreenPos[a].x, g_jointScreenPos[a].y, NULL);
        LineTo(hdc, g_jointScreenPos[b].x, g_jointScreenPos[b].y);
    }

    const int JOINT_RADIUS = 5;
    for (int i = 0; i < NUI_SKELETON_POSITION_COUNT; ++i)
    {
        if (!g_jointValid[i])
            continue;

        int x = g_jointScreenPos[i].x;
        int y = g_jointScreenPos[i].y;
        Ellipse(hdc, x - JOINT_RADIUS, y - JOINT_RADIUS, x + JOINT_RADIUS, y + JOINT_RADIUS);
    }

    SelectObject(hdc, oldPen);
    SelectObject(hdc, oldBrush);
    DeleteObject(bonePen);
    DeleteObject(jointBrush);
}

// Affiche le texte d'etat (zone / posture) sur un bandeau semi-oppaque
// en haut de la fenetre video, pour rester lisible quel que soit le
// contenu de l'image derriere.
static void DrawStatusOverlay(HDC hdc)
{
    RECT textRect = { 8, 8, VIDEO_WIDTH - 8, 34 };

    HBRUSH bgBrush = CreateSolidBrush(RGB(0, 0, 0));
    FillRect(hdc, &textRect, bgBrush);
    DeleteObject(bgBrush);

    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, RGB(255, 255, 0));
    TextOutA(hdc, 12, 12, g_overlayStatusText, static_cast<int>(lstrlenA(g_overlayStatusText)));
}

static LRESULT CALLBACK VideoWindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg)
    {
        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            StretchDIBits(
                hdc,
                0, 0, VIDEO_WIDTH, VIDEO_HEIGHT,
                0, 0, VIDEO_WIDTH, VIDEO_HEIGHT,
                g_videoBuffer.data(),
                &g_bmi,
                DIB_RGB_COLORS,
                SRCCOPY);

            DrawSkeletonOverlay(hdc);
            DrawStatusOverlay(hdc);

            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_CLOSE:
            // On ne detruit pas la fenetre : on la cache et on remet le
            // booleen a false, comme si l'utilisateur avait appuye sur V.
            g_videoStreamEnabled = false;
            ShowWindow(hwnd, SW_HIDE);
            return 0;

        case WM_DESTROY:
            g_hVideoWindow = NULL;
            return 0;
    }

    return DefWindowProc(hwnd, msg, wParam, lParam);
}

// Cree la fenetre video (cachee) une seule fois, au demarrage.
static bool CreateVideoWindow(HINSTANCE hInstance)
{
    WNDCLASS wc = {};
    wc.lpfnWndProc   = VideoWindowProc;
    wc.hInstance     = hInstance;
    wc.lpszClassName = VIDEO_WINDOW_CLASS;
    wc.hCursor       = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);

    if (!RegisterClass(&wc))
    {
        std::cerr << "Impossible d'enregistrer la classe de fenetre video.\n";
        return false;
    }

    RECT r = {0, 0, VIDEO_WIDTH, VIDEO_HEIGHT};
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);

    g_hVideoWindow = CreateWindowEx(
        0,
        VIDEO_WINDOW_CLASS,
        VIDEO_WINDOW_TITLE,
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT,
        r.right - r.left, r.bottom - r.top,
        NULL, NULL, hInstance, NULL);

    if (g_hVideoWindow == NULL)
    {
        std::cerr << "Impossible de creer la fenetre video.\n";
        return false;
    }

    // BITMAPINFO utilise pour chaque StretchDIBits : BGRA 32 bits,
    // hauteur negative = image top-down (evite l'image inversee).
    g_bmi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    g_bmi.bmiHeader.biWidth       = VIDEO_WIDTH;
    g_bmi.bmiHeader.biHeight      = -VIDEO_HEIGHT;
    g_bmi.bmiHeader.biPlanes      = 1;
    g_bmi.bmiHeader.biBitCount    = 32;
    g_bmi.bmiHeader.biCompression = BI_RGB;

    // Reste cachee tant que g_videoStreamEnabled est a false.
    return true;
}

static bool OpenColorStreamIfNeeded()
{
    if (g_colorStreamOpened)
        return true;

    HRESULT hr = g_pNuiSensor->NuiImageStreamOpen(
        NUI_IMAGE_TYPE_COLOR,
        NUI_IMAGE_RESOLUTION_640x480,
        0,
        2,
        g_hNextColorFrameEvent,
        &g_hColorStreamHandle);

    if (FAILED(hr))
    {
        std::cerr << "Impossible d'ouvrir le flux couleur.\n";
        return false;
    }

    g_colorStreamOpened = true;
    return true;
}

// Recupere une nouvelle frame couleur (si disponible) et la copie dans
// g_videoBuffer, puis demande un repaint de la fenetre.
static void ProcessVideoFrame()
{
    static bool windowVisible = false;

    if (!g_videoStreamEnabled)
    {
        if (windowVisible)
        {
            ShowWindow(g_hVideoWindow, SW_HIDE);
            windowVisible = false;
        }
        return;
    }

    if (!OpenColorStreamIfNeeded())
        return;

    if (!windowVisible)
    {
        ShowWindow(g_hVideoWindow, SW_SHOW);
        windowVisible = true;
    }

    if (WaitForSingleObject(g_hNextColorFrameEvent, 0) != WAIT_OBJECT_0)
        return; // pas de nouvelle frame disponible

    NUI_IMAGE_FRAME imageFrame = {0};
    if (FAILED(g_pNuiSensor->NuiImageStreamGetNextFrame(g_hColorStreamHandle, 0, &imageFrame)))
        return;

    INuiFrameTexture* pTexture = imageFrame.pFrameTexture;
    NUI_LOCKED_RECT lockedRect;
    pTexture->LockRect(0, &lockedRect, NULL, 0);

    if (lockedRect.Pitch != 0)
    {
        // Copie ligne par ligne au cas ou le pitch ne correspondrait pas
        // exactement a VIDEO_WIDTH * 4 (marge de securite).
        const int rowBytes = VIDEO_WIDTH * 4;
        for (int y = 0; y < VIDEO_HEIGHT; ++y)
        {
            memcpy(
                g_videoBuffer.data() + y * rowBytes,
                lockedRect.pBits + y * lockedRect.Pitch,
                rowBytes);
        }

        InvalidateRect(g_hVideoWindow, NULL, FALSE);
    }

    pTexture->UnlockRect(0);
    g_pNuiSensor->NuiImageStreamReleaseFrame(g_hColorStreamHandle, &imageFrame);
}

// Fait tourner la pompe de messages de la fenetre video (necessaire
// pour qu'elle reste reactive : deplacement, WM_PAINT, fermeture...).
static void PumpVideoWindowMessages()
{
    MSG msg;
    while (PeekMessage(&msg, NULL, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }
}

// --- Initialisation -------------------------------------------------

bool InitKinect()
{
    int sensorCount = 0;
    if (FAILED(NuiGetSensorCount(&sensorCount)) || sensorCount < 1)
    {
        std::cerr << "Aucune Kinect detectee.\n";
        return false;
    }

    if (FAILED(NuiCreateSensorByIndex(0, &g_pNuiSensor)))
    {
        std::cerr << "Impossible de creer le capteur.\n";
        return false;
    }

    // On active a la fois le squelette et la couleur : le flux couleur
    // ne sera reellement ouvert/affiche que si g_videoStreamEnabled est
    // a true, mais le flag doit etre pose des NuiInitialize.
    HRESULT hr = g_pNuiSensor->NuiInitialize(
        NUI_INITIALIZE_FLAG_USES_SKELETON | NUI_INITIALIZE_FLAG_USES_COLOR);
    if (FAILED(hr))
    {
        std::cerr << "NuiInitialize a echoue (verifie l'alimentation externe "
                     "et le port USB).\n";
        return false;
    }

    g_hNextSkeletonEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
    hr = g_pNuiSensor->NuiSkeletonTrackingEnable(g_hNextSkeletonEvent, 0);
    if (FAILED(hr))
    {
        std::cerr << "Impossible d'activer le tracking squelette.\n";
        return false;
    }

    g_hNextColorFrameEvent = CreateEvent(NULL, TRUE, FALSE, NULL);

    if (!CreateVideoWindow(GetModuleHandle(NULL)))
        return false;

    return true;
}

// Traite une frame de squelette. Se verrouille sur le TrackingID du
// premier squelette tracke rencontre et ignore tout autre joueur qui
// entrerait dans le champ tant que celui-ci reste visible. Si le
// joueur verrouille sort du champ (ou devient trop loin, voir le
// filtrage par profondeur), le verrou est relache et le prochain
// squelette tracke et suffisamment proche prend sa place.
//
// Pour chaque frame du joueur verrouille :
//   - calcule la zone gauche/milieu/droite (axe X) et la posture
//     debout/saut/accroupi (axe Y, relative a la reference calibree)
//   - sur CHANGEMENT d'etat, envoie la commande clavier correspondant
//     a la transition (voir SendZoneMovement / SendPostureCommand)
//   - gere le maintien de la touche 'S' sur sejour prolonge en MILIEU
//   - met a jour la projection du squelette et le texte d'etat pour
//     l'overlay video (voir UpdateSkeletonOverlay / g_overlayStatusText)
//   - affiche l'etat courant dans la console (sur changement)
void ProcessSkeletonFrame()
{
    NUI_SKELETON_FRAME skeletonFrame = {0};
    if (FAILED(g_pNuiSensor->NuiSkeletonGetNextFrame(0, &skeletonFrame)))
        return;

    static Zone lastZone;
    static bool hasLastZone = false;

    static Posture lastPosture;
    static bool hasLastPosture = false;

    static MiddleHoldState middleHoldState = MiddleHoldState::IDLE;
    static DWORD           middleHoldTick  = 0; // sert a la fois pour l'entree en MILIEU et pour le debut du hold

    const NUI_SKELETON_DATA* pLocked = NULL;

    // Cherche le squelette deja verrouille dans cette frame
    if (g_lockedTrackingID != 0)
    {
        for (int i = 0; i < NUI_SKELETON_COUNT; ++i)
        {
            const NUI_SKELETON_DATA& s = skeletonFrame.SkeletonData[i];
            if (IsSkeletonStateUsable(s.eTrackingState) &&
                s.dwTrackingID == g_lockedTrackingID)
            {
                // Le squelette verrouille est retrouve dans cette frame,
                // mais s'il est desormais trop loin, on le traite comme
                // perdu (pLocked reste NULL -> "Joueur perdu" ci-dessous).
                if (IsSkeletonWithinDepthRange(s))
                    pLocked = &s;

                break; // ID trouve, inutile de continuer la recherche
            }
        }

        if (pLocked == NULL)
        {
            // Le joueur verrouille a quitte le champ (ou est trop loin) :
            // on relache le verrou.
            std::cout << "Joueur perdu, en attente d'un nouveau squelette...\n";
            g_lockedTrackingID = 0;
            hasLastZone     = false;
            hasLastPosture  = false;
            ResetYCalibration();
            g_hasSkeletonOverlay = false;
            if (middleHoldState == MiddleHoldState::HOLDING)
                SendKeyCommand("start_up"); // evite de laisser 'S' enfoncee si le joueur disparait
            middleHoldState = MiddleHoldState::IDLE;
            snprintf(g_overlayStatusText, sizeof(g_overlayStatusText), "En attente d'un squelette...");
        }
    }

    // Pas de squelette verrouille : on prend le premier tracke rencontre
    // qui soit egalement dans la plage de profondeur autorisee.
    if (g_lockedTrackingID == 0)
    {
        for (int i = 0; i < NUI_SKELETON_COUNT; ++i)
        {
            const NUI_SKELETON_DATA& s = skeletonFrame.SkeletonData[i];
            if (IsSkeletonStateUsable(s.eTrackingState) &&
                IsSkeletonWithinDepthRange(s))
            {
                g_lockedTrackingID = s.dwTrackingID;
                pLocked = &s;
                std::cout << "Squelette verrouille (TrackingID = "
                          << g_lockedTrackingID << ", z = "
                          << s.SkeletonPositions[NUI_SKELETON_POSITION_HIP_CENTER].z
                          << " m)\n";
                ResetYCalibration();
                break;
            }
        }
    }

    if (pLocked == NULL)
    {
        g_hasSkeletonOverlay = false;
        return; // personne de tracke (ou trop loin) pour l'instant
    }

    // hip_center = joint le plus stable pour suivre la position
    // globale du corps (en metres, X negatif = gauche du capteur,
    // X positif = droite du capteur, Y positif = vers le haut)
    const Vector4& hipCenter = pLocked->SkeletonPositions[NUI_SKELETON_POSITION_HIP_CENTER];
    float currentX = hipCenter.x;
    float currentY = hipCenter.y;

    // Met a jour la projection du squelette pour l'overlay video,
    // independamment de la calibration Y ci-dessous.
    UpdateSkeletonOverlay(*pLocked);
    if (g_hVideoWindow != NULL)
        InvalidateRect(g_hVideoWindow, NULL, FALSE);

    // --- Calibration de la reference Y --------------------------------
    // Pendant les premieres frames suivant le verrouillage, on suppose
    // que le joueur est en position neutre (debout) et on moyenne sa
    // position Y pour en faire la reference du "sol" de detection.
    if (!g_hasBaselineY)
    {
        g_calibrationSum += currentY;
        ++g_calibrationSamples;

        if (g_calibrationSamples >= CALIBRATION_FRAME_COUNT)
        {
            g_baselineHipY  = g_calibrationSum / g_calibrationSamples;
            g_hasBaselineY  = true;
            std::cout << "Reference posture calibree (y = "
                       << g_baselineHipY << " m)\n";
        }
        else
        {
            // Tant que la calibration n'est pas terminee, on ne peut pas
            // encore evaluer la posture de facon fiable.
            snprintf(g_overlayStatusText, sizeof(g_overlayStatusText), "Calibration en cours...");
            return;
        }
    }

    Zone currentZone       = GetZoneFromX(currentX);
    Posture currentPosture = GetPostureFromY(currentY, g_baselineHipY);
    float  deltaY          = currentY - g_baselineHipY;

    snprintf(g_overlayStatusText, sizeof(g_overlayStatusText),
             "Zone: %s (x=%.2fm)  Posture: %s (dy=%.2fm)",
             ZoneToString(currentZone), currentX,
             PostureToString(currentPosture), deltaY);

    // Mode debug : affiche le delta Y brut a CHAQUE frame, meme sans
    // franchissement de seuil. Sert a lire les valeurs reelles produites
    // par un saut/accroupissement pour regler JUMP_Y_THRESHOLD et
    // CROUCH_Y_THRESHOLD en connaissance de cause.
    if (DEBUG_POSTURE_DELTA)
    {
        std::cout << "[debug] deltaY = " << deltaY
                   << " m (y = " << currentY
                   << ", ref = " << g_baselineHipY << ")\n";
    }

    // --- Envoi des commandes clavier bases sur la TRANSITION d'etat ---
    // On ne memorise/compare que l'etat precedent vs l'etat recu : on
    // n'envoie jamais un etat absolu, seulement le mouvement qui permet
    // de passer de l'un a l'autre.
    if (hasLastZone && currentZone != lastZone)
        SendZoneMovement(lastZone, currentZone);

    if (!hasLastPosture || currentPosture != lastPosture)
        SendPostureCommand(currentPosture); // no-op automatique si DEBOUT

    // --- Maintien de 'S' sur sejour prolonge en zone MILIEU -------------
    if (currentZone == Zone::MILIEU)
    {
        DWORD now = GetTickCount();

        switch (middleHoldState)
        {
            case MiddleHoldState::IDLE:
                // Premiere frame en MILIEU : demarre le chronometre d'attente.
                middleHoldTick  = now;
                middleHoldState = MiddleHoldState::WAITING;
                break;

            case MiddleHoldState::WAITING:
                if (now - middleHoldTick >= MIDDLE_HOLD_BEFORE_PRESS_MS)
                {
                    SendKeyCommand("start_down");
                    std::cout << "Maintien MILIEU >= 3s : touche S enfoncee\n";
                    middleHoldTick  = now; // redemarre le chrono pour la duree du hold
                    middleHoldState = MiddleHoldState::HOLDING;
                }
                break;

            case MiddleHoldState::HOLDING:
                if (now - middleHoldTick >= MIDDLE_HOLD_DURATION_MS)
                {
                    SendKeyCommand("start_up");
                    std::cout << "Fin du maintien : touche S relachee\n";
                    middleHoldState = MiddleHoldState::RELEASED;
                }
                break;
            
            case MiddleHoldState::RELEASED:
                // Touche relachee : si le joueur est toujours en MILIEU,
                // on relance le cycle de detection apres une courte pause
                // (evite un redeclenchement instantane), plutot que de
                // rester bloque en RELEASED pour le reste du sejour.
                if (now - middleHoldTick >= MIDDLE_HOLD_RETRIGGER_DELAY_MS)
                {
                    middleHoldTick  = now;
                    middleHoldState = MiddleHoldState::WAITING;
                }
                break;

            default:
                break; // deja joue pour ce sejour en MILIEU, rien a faire
        }
    }
    else
    {
        // Le joueur a quitte la zone MILIEU : si la touche etait
        // enfoncee, on la relache immediatement pour ne jamais la
        // laisser bloquee, puis on reinitialise pour un futur sejour.
        if (middleHoldState == MiddleHoldState::HOLDING)
            SendKeyCommand("start_up");

        middleHoldState = MiddleHoldState::IDLE;
    }

    // N'affiche que lors d'un changement de zone ou de posture pour ne
    // pas spammer la console ; retire ces conditions si tu veux l'etat
    // a chaque frame.
    if (!hasLastZone || currentZone != lastZone ||
        !hasLastPosture || currentPosture != lastPosture)
    {
        std::cout << "Zone : " << ZoneToString(currentZone)
                   << " (x = " << currentX << " m)"
                   << " | Posture : " << PostureToString(currentPosture)
                   << " (y = " << currentY
                   << " m, ref = " << g_baselineHipY << " m)\n";
    }

    lastZone       = currentZone;
    hasLastZone    = true;
    lastPosture    = currentPosture;
    hasLastPosture = true;
}

int main()
{
    if (!InitWinsock())
        return 1;

    if (!InitKinect())
    {
        WSACleanup();
        return 1;
    }

    // Premiere tentative de connexion au serveur clavier Python ; si le
    // serveur n'est pas encore lance, PollKeyboardConnection() retentera
    // automatiquement dans la boucle principale.
    TryConnectKeyboardServer();

    std::cout << "Kinect initialisee, en attente de squelette... "
                 "(Ctrl+C pour quitter, V pour activer/desactiver la video)\n";

    bool vKeyWasDown = false;

    while (true)
    {
        if (WaitForSingleObject(g_hNextSkeletonEvent, 30) == WAIT_OBJECT_0)
            ProcessSkeletonFrame();

        // Bascule le flux video sur appui de la touche 'V'
        bool vKeyIsDown = (GetAsyncKeyState('V') & 0x8000) != 0;
        if (vKeyIsDown && !vKeyWasDown)
        {
            g_videoStreamEnabled = !g_videoStreamEnabled;
            std::cout << "Flux video " << (g_videoStreamEnabled ? "ACTIVE" : "DESACTIVE") << "\n";
        }
        vKeyWasDown = vKeyIsDown;

        ProcessVideoFrame();
        PumpVideoWindowMessages();
        PollKeyboardConnection();
    }

    g_pNuiSensor->NuiShutdown();
    CleanupNetwork();
    return 0;
}

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
// (StretchDIBits) -- aucune dependance externe type OpenCV.
//
// NOUVEAU : le programme se connecte en TCP (127.0.0.1:5000, non
// bloquant, avec reconnexion automatique) a un serveur Python (pynput)
// qui traduit des commandes texte ("haut", "bas", "gauche", "droite"...)
// en appuis clavier reels. On n'envoie PAS l'etat absolu (zone/posture)
// mais uniquement le mouvement correspondant a la TRANSITION d'etat :
//   - gauche -> milieu ou milieu -> droite  => "droite"
//   - droite -> milieu ou milieu -> gauche  => "gauche"
//   - (peu importe l'etat de depart) -> saut     => "haut"
//   - (peu importe l'etat de depart) -> accroupi => "bas"
//   - -> debout : rien n'est envoye (position neutre = pas d'action)
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
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <unknwn.h> 
#include <NuiApi.h>
#include <iostream>
#include <string>
#include <vector>

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
static const float JUMP_Y_THRESHOLD    =  0.06f; // au-dessus -> SAUT
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

// --- Communication reseau avec le serveur clavier (Python) --------------
//
// Le script Python (pynput) ecoute en TCP sur 127.0.0.1:5000 et attend
// des commandes texte terminees par '\n' (ex: "gauche\n", "haut\n").
// Chaque commande recue declenche une pression + relachement de la
// touche clavier correspondante (voir le dictionnaire KEYS du script).
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

// Envoie une commande texte (ex: "haut", "gauche"...) au serveur Python.
// Ne fait rien si pas connecte. En cas d'echec d'envoi (serveur ferme
// la connexion), on repasse en mode "deconnecte" pour retenter plus
// tard via PollKeyboardConnection().
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
// joueur verrouille sort du champ, le verrou est relache et le
// prochain squelette tracke prend sa place.
//
// Pour chaque frame du joueur verrouille :
//   - calcule la zone gauche/milieu/droite (axe X) et la posture
//     debout/saut/accroupi (axe Y, relative a la reference calibree)
//   - sur CHANGEMENT d'etat, envoie la commande clavier correspondant
//     a la transition (voir SendZoneMovement / SendPostureCommand)
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
                pLocked = &s;
                break;
            }
        }

        if (pLocked == NULL)
        {
            // Le joueur verrouille a quitte le champ : on relache le verrou
            std::cout << "Joueur perdu, en attente d'un nouveau squelette...\n";
            g_lockedTrackingID = 0;
            hasLastZone     = false;
            hasLastPosture  = false;
            ResetYCalibration();
        }
    }

    // Pas de squelette verrouille : on prend le premier tracke rencontre
    if (g_lockedTrackingID == 0)
    {
        for (int i = 0; i < NUI_SKELETON_COUNT; ++i)
        {
            const NUI_SKELETON_DATA& s = skeletonFrame.SkeletonData[i];
            if (IsSkeletonStateUsable(s.eTrackingState))
            {
                g_lockedTrackingID = s.dwTrackingID;
                pLocked = &s;
                std::cout << "Squelette verrouille (TrackingID = "
                          << g_lockedTrackingID << ")\n";
                ResetYCalibration();
                break;
            }
        }
    }

    if (pLocked == NULL)
        return; // personne de tracke pour l'instant

    // hip_center = joint le plus stable pour suivre la position
    // globale du corps (en metres, X negatif = gauche du capteur,
    // X positif = droite du capteur, Y positif = vers le haut)
    const Vector4& hipCenter = pLocked->SkeletonPositions[NUI_SKELETON_POSITION_HIP_CENTER];
    float currentX = hipCenter.x;
    float currentY = hipCenter.y;

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
            return;
        }
    }

    Zone currentZone       = GetZoneFromX(currentX);
    Posture currentPosture = GetPostureFromY(currentY, g_baselineHipY);
    float  deltaY          = currentY - g_baselineHipY;

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

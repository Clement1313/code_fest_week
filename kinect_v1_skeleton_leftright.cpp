// kinect_v1_skeleton_leftright.cpp
//
// Recupere le flux squelette de la Kinect v1 (modele 1414) via le SDK
// Kinect for Windows v1.8, et affiche dans quelle zone (gauche, milieu,
// droite) se trouve le joueur en fonction de la position X absolue du
// joint hip_center.
//
// Un flux video couleur peut etre active/desactive a tout moment en
// appuyant sur la touche 'V' (variable booleenne g_videoStreamEnabled).
// L'affichage se fait dans une fenetre Win32 native, dessinee avec GDI
// (StretchDIBits) -- aucune dependance externe type OpenCV.
//
// Setup du projet Visual Studio :
//   - Includes : $(KINECTSDK10_DIR)inc
//   - Lib dirs : $(KINECTSDK10_DIR)lib\x86  (ou \x64 selon la config)
//   - Linker > Input > Additional Dependencies : Kinect10.lib
//   (Gdi32.lib et User32.lib sont deja lies par defaut dans un projet
//    Win32/Console standard)
//   ($(KINECTSDK10_DIR) est une variable d'environnement definie
//    automatiquement par l'installeur du SDK)

#include <windows.h>
#include <NuiApi.h>
#include <iostream>
#include <vector>

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

enum class Zone { GAUCHE, MILIEU, DROITE };

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
void ProcessSkeletonFrame()
{
    NUI_SKELETON_FRAME skeletonFrame = {0};
    if (FAILED(g_pNuiSensor->NuiSkeletonGetNextFrame(0, &skeletonFrame)))
        return;

    static Zone lastZone;
    static bool hasLastZone = false;

    const NUI_SKELETON_DATA* pLocked = NULL;

    // Cherche le squelette deja verrouille dans cette frame
    if (g_lockedTrackingID != 0)
    {
        for (int i = 0; i < NUI_SKELETON_COUNT; ++i)
        {
            const NUI_SKELETON_DATA& s = skeletonFrame.SkeletonData[i];
            if (s.eTrackingState == NUI_SKELETON_TRACKED &&
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
            hasLastZone = false;
        }
    }

    // Pas de squelette verrouille : on prend le premier tracke rencontre
    if (g_lockedTrackingID == 0)
    {
        for (int i = 0; i < NUI_SKELETON_COUNT; ++i)
        {
            const NUI_SKELETON_DATA& s = skeletonFrame.SkeletonData[i];
            if (s.eTrackingState == NUI_SKELETON_TRACKED)
            {
                g_lockedTrackingID = s.dwTrackingID;
                pLocked = &s;
                std::cout << "Squelette verrouille (TrackingID = "
                          << g_lockedTrackingID << ")\n";
                break;
            }
        }
    }

    if (pLocked == NULL)
        return; // personne de tracke pour l'instant

    // hip_center = joint le plus stable pour suivre la position
    // globale du corps (en metres, X negatif = gauche du capteur,
    // X positif = droite du capteur)
    const Vector4& hipCenter = pLocked->SkeletonPositions[NUI_SKELETON_POSITION_HIP_CENTER];
    float currentX = hipCenter.x;

    Zone currentZone = GetZoneFromX(currentX);

    // N'affiche que lors d'un changement de zone pour ne pas spammer la
    // console ; retire cette condition si tu veux l'etat a chaque frame.
    if (!hasLastZone || currentZone != lastZone)
    {
        std::cout << "Zone : " << ZoneToString(currentZone)
                   << " (x = " << currentX << " m)\n";
    }

    lastZone    = currentZone;
    hasLastZone = true;
}

int main()
{
    if (!InitKinect())
        return 1;

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
    }

    g_pNuiSensor->NuiShutdown();
    return 0;
}

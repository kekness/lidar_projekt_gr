using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using Raylib_cs;
using System.IO; 
using System.Windows.Forms; 
using System.Threading;
using System.Linq; 
using System.Collections.Generic;

// === KONFIGURACJA ===
const string OutputFolder = "frames";
const int MaxPointsPerFrame = 50000;

// === PRZYGOTOWANIE VLP-16 ===
double[] VerticalAngles = [-15, 1, -13, -3, -11, 5, -9, 7, -7, 9, -5, 11, -3, 13, -1, 15];
double[] VerticalAnglesRad = VerticalAngles.Select(d => d * Math.PI / 180.0).ToArray();
const double DistanceResolution = 0.002;

// Folder na pliki
if (Directory.Exists(OutputFolder)) Directory.Delete(OutputFolder, true);
Directory.CreateDirectory(OutputFolder);

// === INICJALIZACJA OKNA 3D (RAYLIB) ===
Raylib.InitWindow(1600, 900, "LiDAR Live Viewer (.NET 9)");
Raylib.SetTargetFPS(60); 

// Ustawienie kamery
Camera3D camera = new();
camera.Position = new Vector3(10.0f, 10.0f, 10.0f);
camera.Target = new Vector3(0.0f, 0.0f, 0.0f);
camera.Up = new Vector3(0.0f, 1.0f, 0.0f);
camera.FovY = 45.0f;
camera.Projection = CameraProjection.Perspective;

// === ZMIENNE STANU ===
List<Vector3> buildingFrame = new(MaxPointsPerFrame);
List<Vector3> displayFrame = new(MaxPointsPerFrame);
double lastAzimuth = -1.0;
int frameCount = 0;
bool pcapFinished = false;
bool isLoaded = false; 
bool isLiveCapture = false; 

// Stan kamery
bool isOrbitalMode = false;

CaptureFileReaderDevice? fileDevice = null;
ICaptureDevice? liveDevice = null;
PacketCapture pCapture;

string currentPcapPath = ""; 

// === FUNKCJA RESTARTU (Zmieniona, by obsługiwała Live i File) ===
void RestartCapture(string source, bool liveMode)
{
    // Zamykanie i czyszczenie poprzednich urządzeń
    if (fileDevice != null) { fileDevice.Close(); fileDevice.Dispose(); fileDevice = null; }
    if (liveDevice != null) { liveDevice.StopCapture(); liveDevice.Close(); liveDevice = null; }

    // Resetowanie zmiennych stanu
    buildingFrame.Clear();
    displayFrame.Clear();
    lastAzimuth = -1.0;
    frameCount = 0;
    pcapFinished = false;
    isLoaded = false;
    isLiveCapture = liveMode;

    try
    {
        if (liveMode)
        {
            // 1. TRYB LIVE CAPTURE (źródłem jest Nazwa interfejsu)
            var devices = CaptureDeviceList.Instance;
            ICaptureDevice selectedDevice = devices.Single(d => d.Name == source);
            
            liveDevice = selectedDevice;
            liveDevice.Open(DeviceModes.Promiscuous, 1000); 
            liveDevice.Filter = "udp and dst port 2368";    
            
            liveDevice.OnPacketArrival += (sender, e) => { ProcessPacket(e.GetPacket()); };
            liveDevice.StartCapture();

            currentPcapPath = ""; 
            isLoaded = true;
            Console.WriteLine($"\n🔄 Start Live Capture na interfejsie: {selectedDevice.Description}");
        }
        else
        {
            // 2. TRYB CZYTANIA PLIKU (źródłem jest ścieżka pliku)
            currentPcapPath = source;
            fileDevice = new CaptureFileReaderDevice(source);
            fileDevice.Open();
            isLoaded = true; 
            Console.WriteLine($"\n🔄 Zaladowano: {Path.GetFileName(source)}. Rozpoczeto odczyt.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ Blad ladowania: {ex.Message}");
        isLoaded = false;
        fileDevice = null;
        liveDevice = null;
    }
}

// === METODA WĄTKOWA DLA OpenFileDialog ===
static string OpenSystemFileDialog()
{
    string selectedPath = "";
    
    Thread thread = new Thread(() =>
    {
        using var openFileDialog = new OpenFileDialog();
        openFileDialog.Filter = "PCAP Files (*.pcap, *.pcapng)|*.pcap;*.pcapng|All files (*.*)|*.*";
        openFileDialog.Title = "Wybierz plik z danymi LiDAR (PCAP)";

        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            selectedPath = openFileDialog.FileName;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join(); 

    return selectedPath;
}

// === FUNKCJE OBSŁUGUJĄCE WYBÓR TRYBU ===

void StartFileSelection()
{
    Raylib.EnableCursor(); 
    string filePath = OpenSystemFileDialog();

    if (!string.IsNullOrEmpty(filePath))
    {
        RestartCapture(filePath, false); // false = File Mode
    }
    else
    {
        Console.WriteLine("Anulowano wybor pliku.");
    }
    
    if (!isOrbitalMode) Raylib.DisableCursor();
}

// 🟢 FUNKCJA LIVE CAPTURE Z POPRAWNYM RZUTOWANIEM I AUTOMATYZACJĄ
void StartLiveSelection()
{
    var devices = CaptureDeviceList.Instance;

    if (devices.Count < 1)
    {
        Console.WriteLine("\n❌ Nie znaleziono interfejsow sieciowych do nasluchu. Sprawdz, czy masz zainstalowany WinPcap/Npcap.");
        return;
    }

    // Rzutowanie na LibPcapLiveDevice, aby uzyskać dostęp do Addresses i Description
    var liveDevices = devices.OfType<LibPcapLiveDevice>().ToList();

    if (liveDevices.Count < 1)
    {
        Console.WriteLine("\n❌ Nie znaleziono interfejsow LibPcap do nasluchu.");
        return;
    }

    // --- LOGIKA AUTOMATYCZNEGO WYBORU ---
    LibPcapLiveDevice? autoSelectedDevice = liveDevices
        .FirstOrDefault(d => d.Description.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) == -1 && 
                             d.Description.IndexOf("Tunnel", StringComparison.OrdinalIgnoreCase) == -1 && 
                             d.Addresses.Count > 0);
    
    if (autoSelectedDevice != null)
    {
        Console.WriteLine($"\n✅ Wybrano interfejs automatycznie: {autoSelectedDevice.Description}");
        RestartCapture(autoSelectedDevice.Name, true); 
        return;
    }
    // --- KONIEC LOGIKI AUTOMATYCZNEGO WYBORU ---


    // FALLBACK: Ręczny wybór w konsoli
    Console.WriteLine("\n==============================================");
    Console.WriteLine("🌐 Nie udalo sie wybrac interfejsu automatycznie.");
    Console.WriteLine("   Wpisz numer interfejsu recznie:");
    Console.WriteLine("==============================================");
    int i = 0;
    foreach (var dev in liveDevices)
    {
        Console.WriteLine($"[{i}] {dev.Description} (Name: {dev.Name})");
        i++;
    }
    Console.Write("Wpisz numer interfejsu: ");

    Raylib.EnableCursor();

    string? input = Console.ReadLine();

    if (int.TryParse(input, out int index) && index >= 0 && index < liveDevices.Count)
    {
        RestartCapture(liveDevices[index].Name, true);
    }
    else
    {
        Console.WriteLine("Niepoprawny wybor lub anulowano.");
    }

    if (!isOrbitalMode) Raylib.DisableCursor();
}

// 🟢 Funkcja wyświetlająca ekran startowy
void HandleInitialSelection()
{
    int screenW = Raylib.GetScreenWidth();
    int screenH = Raylib.GetScreenHeight();

    Raylib.BeginDrawing();
    Raylib.ClearBackground(Raylib_cs.Color.Black);

    Raylib.DrawText("LiDAR Viewer: Wybierz tryb dzialania", screenW/2 - 250, screenH/2 - 50, 30, Raylib_cs.Color.White);
    Raylib.DrawText("[1] Wczytaj plik (.pcap)", screenW/2 - 150, screenH/2 + 20, 25, Raylib_cs.Color.SkyBlue);
    Raylib.DrawText("[2] Transmisja na zywo (Live Capture)", screenW/2 - 150, screenH/2 + 70, 25, Raylib_cs.Color.Green);
    Raylib.DrawText("Nacisnij [ESC] aby wyjsc", screenW/2 - 120, screenH - 50, 20, Raylib_cs.Color.DarkGray);
    
    Raylib.EndDrawing();

    if (Raylib.IsKeyPressed(KeyboardKey.One))
    {
        StartFileSelection();
    }
    else if (Raylib.IsKeyPressed(KeyboardKey.Two))
    {
        StartLiveSelection();
    }
}

// 🟢 NOWA FUNKCJA DO COFANIA DO MENU
void GoToMenu()
{
    // Zamykanie aktywnego urządzenia
    if (fileDevice != null) { fileDevice.Close(); fileDevice.Dispose(); fileDevice = null; }
    if (liveDevice != null) { liveDevice.StopCapture(); liveDevice.Close(); liveDevice = null; }

    // Resetowanie stanu, co wymusi powrót do HandleInitialSelection w głównej pętli
    isLoaded = false;
    isLiveCapture = false;
    currentPcapPath = "";
    frameCount = 0;

    Console.WriteLine("\n◀️ Powrot do menu glownego.");
}

Console.WriteLine("🟢 Uruchomiono. Czekam na wybor trybu...");

// === GŁÓWNA PĘTLA PROGRAMU ===
while (!Raylib.WindowShouldClose())
{
    // Jeśli plik nie jest załadowany, czekaj na wybór trybu
    if (!isLoaded)
    {
        HandleInitialSelection();
        continue; 
    }
    
    // ---------------------------------------------------------
    // 1. Logika PCAP (czytanie pakietów) - TYLKO W TRYBIE FILE
    // ---------------------------------------------------------
    if (!isLiveCapture) 
    {
        int packetsProcessedThisLoop = 0;
        while (fileDevice != null && !pcapFinished && packetsProcessedThisLoop < 50) 
        {
            if (fileDevice.GetNextPacket(out pCapture) != GetPacketStatus.PacketRead)
            {
                pcapFinished = true;
                Console.WriteLine("\n🏁 Koniec pliku PCAP.");
                break;
            }
            ProcessPacket(pCapture.GetPacket());
            packetsProcessedThisLoop++;
        }
    }
    // W trybie Live Capture (isLiveCapture == true) pakiety są pobierane w tle przez event.

    // ---------------------------------------------------------
    // 2. Obsługa wejścia (Kamera + Zmiana Źródła)
    // ---------------------------------------------------------
    
    // 🟢 Klawisz [M] - POWRÓT DO MENU
    if (Raylib.IsKeyPressed(KeyboardKey.M))
    {
        GoToMenu();
    }

    // 🟢 Klawisz [R] - RESTART (TYLKO w trybie plikowym)
    if (Raylib.IsKeyPressed(KeyboardKey.R))
    {
        if (!isLiveCapture && !string.IsNullOrEmpty(currentPcapPath))
        {
            RestartCapture(currentPcapPath, false);
        }
        else if (isLiveCapture)
        {
            Console.WriteLine("\nℹ️ Uzyj [M] aby wrocic do menu wyboru z trybu Live.");
        }
    }
    
    if (Raylib.IsKeyPressed(KeyboardKey.C))
    {
        isOrbitalMode = !isOrbitalMode;
        Raylib.EnableCursor(); 
        if (isOrbitalMode) camera.Target = Vector3.Zero; 
    }

    if (isOrbitalMode)
    {
        Raylib.UpdateCamera(ref camera, CameraMode.Orbital);
    }
    else
    {
        float speed = 0.5f; 
        float sensitivity = 0.05f; 
        Vector3 movement = Vector3.Zero; 
        Vector3 rotation = Vector3.Zero; 

        if (Raylib.IsKeyDown(KeyboardKey.W)) movement.X = speed;
        if (Raylib.IsKeyDown(KeyboardKey.S)) movement.X = -speed;
        if (Raylib.IsKeyDown(KeyboardKey.D)) movement.Y = speed;
        if (Raylib.IsKeyDown(KeyboardKey.A)) movement.Y = -speed;
        if (Raylib.IsKeyDown(KeyboardKey.E)) movement.Z = speed; 
        if (Raylib.IsKeyDown(KeyboardKey.Q)) movement.Z = -speed;

        if (Raylib.IsMouseButtonPressed(MouseButton.Right)) Raylib.DisableCursor();
        
        if (Raylib.IsMouseButtonDown(MouseButton.Right))
        {
            Vector2 mouseDelta = Raylib.GetMouseDelta();
            rotation.X = mouseDelta.X * sensitivity;
            rotation.Y = mouseDelta.Y * sensitivity;
        }
        
        if (Raylib.IsMouseButtonReleased(MouseButton.Right)) Raylib.EnableCursor();

        Raylib.UpdateCameraPro(ref camera, movement, rotation, 0.0f);
    }
    // ---------------------------------------------------------


    // 3. Rysowanie
    Raylib.BeginDrawing();
    Raylib.ClearBackground(Raylib_cs.Color.Black);

        Raylib.BeginMode3D(camera);
            
            // Osie współrzędnych
            Raylib.DrawLine3D(new Vector3(0,0,0), new Vector3(1,0,0), Raylib_cs.Color.Red);
            Raylib.DrawLine3D(new Vector3(0,0,0), new Vector3(0,1,0), Raylib_cs.Color.Green);
            Raylib.DrawLine3D(new Vector3(0,0,0), new Vector3(0,0,1), Raylib_cs.Color.Blue);
            
            // Rysuj punkty chmury
            var pointsToDraw = displayFrame; 
            foreach (var p in pointsToDraw)
            {
                Raylib_cs.Color ptColor = Raylib_cs.Color.Green;
                if (p.Y > 0.5f) ptColor = Raylib_cs.Color.Yellow;
                if (p.Y > 2.0f) ptColor = Raylib_cs.Color.Red;
                Raylib.DrawPoint3D(p, ptColor);
            }

        Raylib.EndMode3D();

        // --- UI / LEGENDA ---
        Raylib.DrawRectangle(10, 80, 260, 240, new Raylib_cs.Color(20, 20, 20, 200)); 
        Raylib.DrawRectangleLines(10, 80, 260, 240, Raylib_cs.Color.DarkGray);

        Raylib.DrawText("STEROWANIE:", 20, 90, 20, Raylib_cs.Color.White);
        
        Raylib.DrawText($"ZRODLO: {(isLiveCapture ? "LIVE" : "PLIK")}", 20, 120, 20, (isLiveCapture ? Raylib_cs.Color.Green : Raylib_cs.Color.SkyBlue));
        Raylib.DrawText($"[C] Tryb: {(isOrbitalMode ? "ORBITAL" : "FREE")}", 20, 150, 20, Raylib_cs.Color.Orange);
        
        // NOWE PRZYCISKI W MENU
        Raylib.DrawText("[M] MENU", 20, 260, 20, Raylib_cs.Color.Pink);
        Raylib.DrawText("[R] RESTART PLIKU", 20, 290, 20, Raylib_cs.Color.Gold);


        int startY = 180;
        if (!isOrbitalMode) 
        {
            Raylib.DrawText("- WASD: Poruszanie", 20, startY, 18, Raylib_cs.Color.LightGray);
            Raylib.DrawText("- Q / E: Gora / Dol", 20, startY + 25, 18, Raylib_cs.Color.LightGray);
            Raylib.DrawText("- PPM (trzymaj): Obrot", 20, startY + 50, 18, Raylib_cs.Color.LightGray);
        }
        else 
        {
            Raylib.DrawText("- Scroll: Zoom", 20, startY, 18, Raylib_cs.Color.LightGray);
        }

        // Podstawowe statystyki
        Raylib.DrawFPS(10, 10);
        Raylib.DrawText($"Frame: {frameCount} | Points: {displayFrame.Count}", 10, 40, 20, Raylib_cs.Color.Gray);
        
        if(pcapFinished) 
            Raylib.DrawText("KONIEC PLIKU", Raylib.GetScreenWidth()/2 - 100, 50, 30, Raylib_cs.Color.Red);
        
        // Wyświetlanie nazwy pliku/urządzenia w rogu
        string sourceName = isLiveCapture ? "LIVE Capture" : Path.GetFileName(currentPcapPath);
        if (!string.IsNullOrEmpty(sourceName))
            Raylib.DrawText(sourceName, Raylib.GetScreenWidth() - 250, 10, 20, Raylib_cs.Color.White);

    Raylib.EndDrawing();
}

// Zamykamy urządzenia PCAP i Live przed zamknięciem okna
if (fileDevice != null)
{
    fileDevice.Close();
    fileDevice.Dispose();
}
if (liveDevice != null)
{
    liveDevice.StopCapture();
    liveDevice.Close();
}
Raylib.CloseWindow();

// === METODY POMOCNICZE (BEZ ZMIAN) ===
void ProcessPacket(RawCapture rawCapture)
{
    var packet = Packet.ParsePacket(rawCapture.LinkLayerType, rawCapture.Data);
    var udpPacket = packet.Extract<UdpPacket>();
    if (udpPacket == null || udpPacket.DestinationPort != 2368) return;

    ReadOnlySpan<byte> raw = udpPacket.PayloadData;
    if (raw.Length != 1206) return;

    for (int block = 0; block < 12; block++)
    {
        int baseOffset = block * 100;
        ushort flag = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(baseOffset, 2));
        if (flag != 0xEEFF) continue;

        ushort azimuthRaw = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(baseOffset + 2, 2));
        double azimuth = azimuthRaw / 100.0;

        // Sekcja budowania klatki
        if (lastAzimuth > 350.0 && azimuth < 10.0)
        {
            displayFrame = new List<Vector3>(buildingFrame);
            
            if (!isLiveCapture) 
            {
                string filename = Path.Combine(OutputFolder, $"frame_{frameCount:D4}.ply");
                SaveToPly(filename, buildingFrame);
            }

            buildingFrame.Clear();
            frameCount++;
        }
        lastAzimuth = azimuth;

        double azimuthRad = azimuth * (Math.PI / 180.0);
        double cosAzimuth = Math.Cos(azimuthRad);
        double sinAzimuth = Math.Sin(azimuthRad);

        for (int channel = 0; channel < 32; channel++)
        {
            int offset = baseOffset + 4 + (channel * 3);
            ushort distanceRaw = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(offset, 2));
            if (distanceRaw == 0) continue;

            double distanceM = distanceRaw * DistanceResolution;
            double vertAngle = VerticalAnglesRad[channel % 16];

            double xyDistance = distanceM * Math.Cos(vertAngle);
            
            float lx = (float)(xyDistance * sinAzimuth);
            float ly = (float)(xyDistance * cosAzimuth);
            float lz = (float)(distanceM * Math.Sin(vertAngle));

            buildingFrame.Add(new Vector3(lx, lz, ly));
        }
    }
}

void SaveToPly(string path, List<Vector3> points)
{
    using StreamWriter writer = new(path);
    writer.WriteLine("ply");
    writer.WriteLine("format ascii 1.0");
    writer.WriteLine($"element vertex {points.Count}");
    writer.WriteLine("property float x");
    writer.WriteLine("property float y");
    writer.WriteLine("property float z");
    writer.WriteLine("end_header");

    var culture = CultureInfo.InvariantCulture;
    foreach (var p in points)
    {
        writer.WriteLine($"{p.X.ToString(culture)} {p.Y.ToString(culture)} {p.Z.ToString(culture)}");
    }
}
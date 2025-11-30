using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using Raylib_cs;

// === KONFIGURACJA ===
const string PcapPath = "3v.pcap";
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
Raylib.SetTargetFPS(60); // Zwiększyłem FPS dla płynności

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

// Stan kamery
bool isOrbitalMode = false; // Startujemy w trybie FREE

// Otwieramy urządzenie PCAP
using var device = new CaptureFileReaderDevice(PcapPath);
device.Open();
PacketCapture pCapture;

Console.WriteLine("🟢 Uruchomiono.");

// === GŁÓWNA PĘTLA PROGRAMU ===
while (!Raylib.WindowShouldClose())
{
    // 1. Logika PCAP (czytanie pakietów)
    int packetsProcessedThisLoop = 0;
    while (!pcapFinished && packetsProcessedThisLoop < 50) 
    {
        if (device.GetNextPacket(out pCapture) != GetPacketStatus.PacketRead)
        {
            pcapFinished = true;
            Console.WriteLine("\n🏁 Koniec pliku PCAP.");
            break;
        }
        ProcessPacket(pCapture.GetPacket());
        packetsProcessedThisLoop++;
    }

    // ---------------------------------------------------------
    // 2. Obsługa kamery (PRZEŁĄCZANIE TRYBÓW)
    // ---------------------------------------------------------
    
    // Klawisz [C] zmienia tryb kamery
    if (Raylib.IsKeyPressed(KeyboardKey.C))
    {
        isOrbitalMode = !isOrbitalMode;
        
        // Ważne: Zawsze odblokuj kursor przy zmianie trybu, żeby nie zniknął
        Raylib.EnableCursor(); 
        
        if (isOrbitalMode)
        {
            // Reset dla orbitala, żeby patrzył na środek
            camera.Target = Vector3.Zero; 
        }
    }

    if (isOrbitalMode)
    {
        // --- TRYB ORBITAL (Wbudowany w Raylib) ---
        // Myszka + Alt = Obrót, Kółko = Zoom
        Raylib.UpdateCamera(ref camera, CameraMode.Orbital);
    }
    else
    {
        // --- TRYB FREE (Nasz własny kod) ---
        float speed = 0.5f;        
        float sensitivity = 0.05f; 

        Vector3 movement = Vector3.Zero; 
        Vector3 rotation = Vector3.Zero; 

        // Klawiatura
        if (Raylib.IsKeyDown(KeyboardKey.W)) movement.X = speed;
        if (Raylib.IsKeyDown(KeyboardKey.S)) movement.X = -speed;
        if (Raylib.IsKeyDown(KeyboardKey.D)) movement.Y = speed;
        if (Raylib.IsKeyDown(KeyboardKey.A)) movement.Y = -speed;
        if (Raylib.IsKeyDown(KeyboardKey.E)) movement.Z = speed; 
        if (Raylib.IsKeyDown(KeyboardKey.Q)) movement.Z = -speed;

        // Myszka (Zablokuj tylko jak kliknięto Prawy Przycisk)
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
    Raylib.ClearBackground(Color.Black);

        Raylib.BeginMode3D(camera);
            
            // Osie współrzędnych
            Raylib.DrawLine3D(new Vector3(0,0,0), new Vector3(1,0,0), Color.Red);
            Raylib.DrawLine3D(new Vector3(0,0,0), new Vector3(0,1,0), Color.Green);
            Raylib.DrawLine3D(new Vector3(0,0,0), new Vector3(0,0,1), Color.Blue);
            
            // Rysuj punkty chmury
            var pointsToDraw = displayFrame; 
            foreach (var p in pointsToDraw)
            {
                Color ptColor = Color.Green;
                if (p.Y > 0.5f) ptColor = Color.Yellow;
                if (p.Y > 2.0f) ptColor = Color.Red;
                Raylib.DrawPoint3D(p, ptColor);
            }

        Raylib.EndMode3D();

        // --- UI / LEGENDA ---
        // Rysujemy tło pod legendą dla czytelności
        Raylib.DrawRectangle(10, 80, 260, 160, new Color(20, 20, 20, 200));
        Raylib.DrawRectangleLines(10, 80, 260, 160, Color.DarkGray);

        Raylib.DrawText("STEROWANIE:", 20, 90, 20, Color.White);
        
        // Zmień kolor aktywnego trybu
        Color cFree = isOrbitalMode ? Color.Gray : Color.Green;
        Color cOrbital = isOrbitalMode ? Color.Green : Color.Gray;

        Raylib.DrawText($"[C] Tryb: {(isOrbitalMode ? "ORBITAL" : "FREE")}", 20, 120, 20, Color.Orange);

        if (!isOrbitalMode) // Legenda dla FREE
        {
            Raylib.DrawText("- WASD: Poruszanie", 20, 150, 18, Color.LightGray);
            Raylib.DrawText("- Q / E: Góra / Dól", 20, 175, 18, Color.LightGray);
            Raylib.DrawText("- PPM (trzymaj): Obrót", 20, 200, 18, Color.LightGray);
        }
        else // Legenda dla ORBITAL
        {
            Raylib.DrawText("- Scroll: Zoom", 20, 150, 18, Color.LightGray);
        }

        // Podstawowe statystyki
        Raylib.DrawFPS(10, 10);
        Raylib.DrawText($"Frame: {frameCount} | Points: {displayFrame.Count}", 10, 40, 20, Color.Gray);
        
        if(pcapFinished) 
            Raylib.DrawText("KONIEC PLIKU", Raylib.GetScreenWidth()/2 - 100, 50, 30, Color.Red);

    Raylib.EndDrawing();
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

        if (lastAzimuth > 350.0 && azimuth < 10.0)
        {
            displayFrame = new List<Vector3>(buildingFrame);
            string filename = Path.Combine(OutputFolder, $"frame_{frameCount:D4}.ply");
            SaveToPly(filename, buildingFrame);
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
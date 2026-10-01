using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class EslabongShinyReroll
{
    private const int RefWidth = 2560;
    private const int RefHeight = 1440;
    private const int HotkeyStart = 1;
    private const int HotkeyStop = 2;
    private const int HotkeyExit = 3;
    private const int WmHotkey = 0x0312;
    private const uint VkF8 = 0x77;
    private const uint VkF10 = 0x79;
    private const uint VkF11 = 0x7A;
    private const uint VkR = 0x52;
    private const uint VkLeft = 0x25;
    private const uint VkRight = 0x27;
    private const uint KeyUp = 0x0002;
    private static volatile bool running;
    private static volatile bool exiting;
    private static Thread worker;
    private static int alertPlaying;
    private static readonly object RecentLock = new object();
    private static readonly Queue<RecentFrame> RecentFrames = new Queue<RecentFrame>();
    private enum GameMode { Normal, Champion }
    private enum ChampionRerollMode { FixedChampion, RotateChampions }
    private enum ChampionStopMode { FirstFound, CompleteSelected }
    private sealed class RunOptions
    {
        public GameMode Mode = GameMode.Normal;
        public ChampionRerollMode ChampionMode = ChampionRerollMode.FixedChampion;
        public ChampionStopMode StopMode = ChampionStopMode.FirstFound;
        public bool SearchChampion = true;
        public bool SearchMercenaries = true;
    }
    private sealed class FoundResult
    {
        public int Slot;
        public string JobName;
        public string FoundPath;
        public int Rolls;
        public TimeSpan Elapsed;
    }
    private static RunOptions Options = new RunOptions();

    private sealed class RecentFrame
    {
        public Bitmap Image;
        public int Roll;
    }

    private sealed class FrameSample : IDisposable
    {
        public Bitmap Image;
        public int ClientWidth;
        public int ClientHeight;
        public Rectangle Roi;
        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr HWnd;
        public uint Msg;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("kernel32.dll")] private static extern bool Beep(uint frequency, uint duration);

    private sealed class Blob
    {
        public int Left = int.MaxValue, Top = int.MaxValue, Right, Bottom, Area;
        public double CenterX { get { return (Left + Right) / 2.0; } }
        public double CenterY { get { return (Top + Bottom) / 2.0; } }
        public int Width { get { return Right - Left + 1; } }
        public int Height { get { return Bottom - Top + 1; } }
    }

    private sealed class Detection
    {
        public bool Found;
        public Blob Character;
        public Blob Marker;
        public double Score;
        public double BrightColorFraction;
        public string Reason;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // 4K + Windows 150% 배율에서도 캡처 좌표와 실제 화면 픽셀을 일치시킨다.
        try
        {
            if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); // PER_MONITOR_AWARE_V2
        }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }

        if (args.Length == 2 && args[0].Equals("--analyze", StringComparison.OrdinalIgnoreCase))
            return AnalyzeFile(args[1]);
        if (args.Length == 2 && args[0].Equals("--analyze-screen", StringComparison.OrdinalIgnoreCase))
            return AnalyzeScreenFile(args[1]);
        if (args.Length == 6 && args[0].Equals("--analyze-region", StringComparison.OrdinalIgnoreCase))
            return AnalyzeRegionFile(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
        if (args.Length == 3 && args[0].Equals("--ocr", StringComparison.OrdinalIgnoreCase))
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(TryReadJobName(args[1], int.Parse(args[2])) ?? "OCR_FAILED");
            return 0;
        }
        if (args.Length == 1 && args[0].Equals("--popup-test", StringComparison.OrdinalIgnoreCase))
        {
            ShowFoundPopup("도적", 3, 317, TimeSpan.FromSeconds(104), null);
            return 0;
        }
        if (args.Length == 3 && args[0].Equals("--popup-image-test", StringComparison.OrdinalIgnoreCase))
        {
            int testSlot = int.Parse(args[2]);
            using (Bitmap preview = CreateSlotPreview(args[1], testSlot))
                ShowFoundPopup("도적", testSlot, 317, TimeSpan.FromSeconds(104), preview);
            return 0;
        }
        if (args.Length == 5 && args[0].Equals("--combined-popup-test", StringComparison.OrdinalIgnoreCase))
        {
            FoundResult championTest = new FoundResult { Slot = int.Parse(args[2]), JobName = "잠꾸러기 거인", FoundPath = args[1], Rolls = 2341, Elapsed = TimeSpan.FromMinutes(16.5) };
            FoundResult mercenaryTest = new FoundResult { Slot = int.Parse(args[4]), JobName = "크루세이더", FoundPath = args[3], Rolls = 5176, Elapsed = TimeSpan.FromMinutes(37.75) };
            using (Bitmap championPreview = CreateSlotPreview(championTest.FoundPath, championTest.Slot))
            using (Bitmap mercenaryPreview = CreateSlotPreview(mercenaryTest.FoundPath, mercenaryTest.Slot))
                ShowCombinedFoundPopup(championTest, mercenaryTest, 7517, TimeSpan.FromMinutes(37.75), championPreview, mercenaryPreview);
            return 0;
        }
        if (args.Length == 1 && args[0].Equals("--diagnose-live", StringComparison.OrdinalIgnoreCase))
            return DiagnoseLive();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!ShowConfigurationDialog()) return 0;

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string modeName = Options.Mode == GameMode.Normal ? "일반 모드" : "챔피언 모드";
        Console.Title = "Eslabong 샤이니 리롤 v24 MULTI MODE - " + modeName;
        Console.WriteLine("Eslabong 샤이니 리롤 매크로 v24 MULTI MODE");
        Console.WriteLine("선택 모드: " + modeName);
        Console.WriteLine("F8 시작 | F10 즉시 중지 | F11 종료");
        Console.WriteLine("게임을 16:9 캐릭터 선택 화면에 두고 F8을 누르세요. 1440p/4K는 자동 보정됩니다.");
        Console.WriteLine("게임 창이 전면이 아니면 자동으로 안전 중지합니다.\n");
        if (Options.Mode == GameMode.Normal)
            Console.WriteLine("TURBO 모드: 바뀌는 슬롯 수에 맞춘 대기 시간으로 밝은 직업명을 확인한 뒤 진행합니다.\n");
        else
        {
            Console.WriteLine("챔피언 탐색: {0} | 일반 용병 탐색: {1}", Options.SearchChampion ? "켜짐" : "꺼짐", Options.SearchMercenaries ? "켜짐" : "꺼짐");
            Console.WriteLine("챔피언 방식: {0}", Options.ChampionMode == ChampionRerollMode.FixedChampion ? "좌우 왕복으로 현재 챔피언 유지" : "오른쪽으로 전체 챔피언 순환");
            Console.WriteLine("종료 방식: {0}\n", Options.StopMode == ChampionStopMode.FirstFound ? "챔피언 또는 일반 용병 중 하나 발견 시 전체 종료" : "발견된 쪽만 고정하고 선택한 대상을 모두 찾을 때까지 계속");
        }

        bool registeredStart = RegisterHotKey(IntPtr.Zero, HotkeyStart, 0, VkF8);
        bool registeredStop = RegisterHotKey(IntPtr.Zero, HotkeyStop, 0, VkF10);
        bool registeredExit = RegisterHotKey(IntPtr.Zero, HotkeyExit, 0, VkF11);
        if (!registeredStart || !registeredStop || !registeredExit)
        {
            if (registeredStart) UnregisterHotKey(IntPtr.Zero, HotkeyStart);
            if (registeredStop) UnregisterHotKey(IntPtr.Zero, HotkeyStop);
            if (registeredExit) UnregisterHotKey(IntPtr.Zero, HotkeyExit);
            Console.WriteLine("단축키 등록에 실패했습니다. F8/F10/F11을 사용하는 다른 프로그램이 있는지 확인하세요.");
            Console.WriteLine("아무 키나 누르면 닫힙니다.");
            Console.ReadKey(true);
            return 2;
        }

        try
        {
            Message msg;
            while (!exiting && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Msg == WmHotkey)
                {
                    int id = unchecked((int)msg.WParam.ToUInt32());
                    if (id == HotkeyStart) Start();
                    else if (id == HotkeyStop) Stop("사용자가 F10을 눌렀습니다.");
                    else if (id == HotkeyExit) { Stop("종료합니다."); exiting = true; break; }
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            running = false;
            UnregisterHotKey(IntPtr.Zero, HotkeyStart);
            UnregisterHotKey(IntPtr.Zero, HotkeyStop);
            UnregisterHotKey(IntPtr.Zero, HotkeyExit);
        }
        return 0;
    }

    private static bool ShowConfigurationDialog()
    {
        using (Form form = new Form())
        using (RadioButton normal = new RadioButton())
        using (RadioButton champion = new RadioButton())
        using (GroupBox championGroup = new GroupBox())
        using (GroupBox cycleGroup = new GroupBox())
        using (GroupBox stopGroup = new GroupBox())
        using (CheckBox searchA = new CheckBox())
        using (CheckBox searchB = new CheckBox())
        using (RadioButton fixedChampion = new RadioButton())
        using (RadioButton rotateChampions = new RadioButton())
        using (RadioButton stopFirst = new RadioButton())
        using (RadioButton stopAll = new RadioButton())
        using (Label expectedTime = new Label())
        using (Button start = new Button())
        using (Button cancel = new Button())
        {
            form.Text = "Eslabong 샤이니 리롤 설정";
            form.ClientSize = new Size(900, 745);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.TopMost = true;
            form.AutoScaleMode = AutoScaleMode.None;

            Label title = new Label();
            title.Text = "게임 모드를 선택하세요";
            title.Font = new Font("맑은 고딕", 17F, FontStyle.Bold);
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.SetBounds(40, 20, 820, 50);

            normal.Text = "일반 모드";
            normal.Font = new Font("맑은 고딕", 12F, FontStyle.Bold);
            normal.SetBounds(225, 85, 190, 35);
            normal.Checked = true;

            champion.Text = "챔피언 모드";
            champion.Font = new Font("맑은 고딕", 12F, FontStyle.Bold);
            champion.SetBounds(515, 85, 210, 35);

            championGroup.Text = "챔피언 모드 세부 설정";
            championGroup.Font = new Font("맑은 고딕", 10.5F, FontStyle.Bold);
            championGroup.SetBounds(45, 135, 810, 420);
            championGroup.Enabled = false;

            searchA.Text = "챔피언 탐색 (1번 슬롯)";
            searchA.Font = new Font("맑은 고딕", 10F, FontStyle.Regular);
            searchA.SetBounds(25, 35, 340, 32);
            searchA.Checked = true;
            searchB.Text = "일반 용병 탐색 (2·3번 슬롯)";
            searchB.Font = new Font("맑은 고딕", 10F, FontStyle.Regular);
            searchB.SetBounds(410, 35, 360, 32);
            searchB.Checked = true;

            cycleGroup.Text = "챔피언 리롤 방식";
            cycleGroup.Font = new Font("맑은 고딕", 10F, FontStyle.Bold);
            cycleGroup.SetBounds(20, 80, 770, 130);
            fixedChampion.Text = "왼쪽 → 오른쪽 왕복 (현재 챔피언 유지)";
            fixedChampion.Font = new Font("맑은 고딕", 10F, FontStyle.Regular);
            fixedChampion.SetBounds(20, 32, 720, 34);
            fixedChampion.Checked = true;
            rotateChampions.Text = "오른쪽만 반복 (챔피언 종류 계속 변경)";
            rotateChampions.Font = new Font("맑은 고딕", 10F, FontStyle.Regular);
            rotateChampions.SetBounds(20, 76, 720, 34);

            stopGroup.Text = "발견 후 동작";
            stopGroup.Font = new Font("맑은 고딕", 10F, FontStyle.Bold);
            stopGroup.SetBounds(20, 225, 770, 165);
            stopFirst.Text = "챔피언 또는 일반 용병 중 하나라도 발견하면 전체 종료";
            stopFirst.Font = new Font("맑은 고딕", 10F, FontStyle.Regular);
            stopFirst.SetBounds(20, 32, 730, 42);
            stopFirst.Checked = true;
            stopAll.Text = "발견된 쪽만 고정하고 나머지를 계속 탐색";
            stopAll.Font = new Font("맑은 고딕", 10F, FontStyle.Regular);
            stopAll.SetBounds(20, 94, 730, 42);

            expectedTime.Font = new Font("맑은 고딕", 10.5F, FontStyle.Bold);
            expectedTime.TextAlign = ContentAlignment.MiddleCenter;
            expectedTime.BorderStyle = BorderStyle.FixedSingle;
            expectedTime.BackColor = Color.FromArgb(247, 247, 247);
            expectedTime.SetBounds(45, 565, 810, 72);

            cycleGroup.Controls.Add(fixedChampion);
            cycleGroup.Controls.Add(rotateChampions);
            stopGroup.Controls.Add(stopFirst);
            stopGroup.Controls.Add(stopAll);

            championGroup.Controls.Add(searchA);
            championGroup.Controls.Add(searchB);
            championGroup.Controls.Add(cycleGroup);
            championGroup.Controls.Add(stopGroup);

            Action updateChampionControls = delegate
            {
                championGroup.Enabled = champion.Checked;
                cycleGroup.Enabled = searchA.Checked;
                stopGroup.Enabled = searchA.Checked && searchB.Checked;
                expectedTime.Text = GetExpectedTimeText(
                    champion.Checked,
                    searchA.Checked,
                    searchB.Checked,
                    fixedChampion.Checked,
                    stopAll.Checked);
            };
            normal.CheckedChanged += delegate { updateChampionControls(); };
            champion.CheckedChanged += delegate { updateChampionControls(); };
            searchA.CheckedChanged += delegate { updateChampionControls(); };
            searchB.CheckedChanged += delegate { updateChampionControls(); };
            fixedChampion.CheckedChanged += delegate { updateChampionControls(); };
            rotateChampions.CheckedChanged += delegate { updateChampionControls(); };
            stopFirst.CheckedChanged += delegate { updateChampionControls(); };
            stopAll.CheckedChanged += delegate { updateChampionControls(); };
            updateChampionControls();

            start.Text = "설정 완료";
            start.Font = new Font("맑은 고딕", 11F, FontStyle.Bold);
            start.SetBounds(295, 655, 150, 52);
            start.DialogResult = DialogResult.OK;
            cancel.Text = "취소";
            cancel.Font = new Font("맑은 고딕", 11F);
            cancel.SetBounds(460, 655, 150, 52);
            cancel.DialogResult = DialogResult.Cancel;

            form.Controls.Add(title);
            form.Controls.Add(normal);
            form.Controls.Add(champion);
            form.Controls.Add(championGroup);
            form.Controls.Add(expectedTime);
            form.Controls.Add(start);
            form.Controls.Add(cancel);
            form.AcceptButton = start;
            form.CancelButton = cancel;

            start.Click += delegate
            {
                if (champion.Checked && !searchA.Checked && !searchB.Checked)
                {
                    MessageBox.Show(form, "챔피언 또는 일반 용병 중 하나 이상을 선택하세요.", "설정 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    form.DialogResult = DialogResult.None;
                }
            };

            if (form.ShowDialog() != DialogResult.OK) return false;
            Options.Mode = champion.Checked ? GameMode.Champion : GameMode.Normal;
            Options.SearchChampion = searchA.Checked;
            Options.SearchMercenaries = searchB.Checked;
            Options.ChampionMode = fixedChampion.Checked ? ChampionRerollMode.FixedChampion : ChampionRerollMode.RotateChampions;
            Options.StopMode = stopFirst.Checked ? ChampionStopMode.FirstFound : ChampionStopMode.CompleteSelected;
            return true;
        }
    }

    private static string GetExpectedTimeText(bool championMode, bool searchChampion, bool searchMercenaries, bool fixedChampion, bool completeSelected)
    {
        const double shinyChance = 1.0 / 5000.0;
        const double normalChecksPerSecond = 3.37;
        const double rotatingChampionChecksPerSecond = 6.23;
        const double mercenaryChecksPerSecond = 4.84;
        const double rotatingBothChecksPerSecond = 3.98;

        double mercenaryChance = 1.0 - Math.Pow(1.0 - shinyChance, 2.0);
        double anyThreeChance = 1.0 - Math.Pow(1.0 - shinyChance, 3.0);
        double seconds;

        if (!championMode)
        {
            seconds = 1.0 / (anyThreeChance * normalChecksPerSecond);
            return "현재 설정 기대시간: 평균 약 " + FormatExpectedDuration(seconds) +
                "\r\n샤이니 확률 1/5000 · 최근 실측 속도 기준";
        }

        if (!searchChampion && !searchMercenaries)
            return "탐색 대상을 하나 이상 선택하세요.";

        double championChecksPerSecond = fixedChampion
            ? rotatingChampionChecksPerSecond / 2.0
            : rotatingChampionChecksPerSecond;
        double bothChecksPerSecond = fixedChampion
            ? 1.0 / (1.0 / rotatingChampionChecksPerSecond + 1.0 / rotatingBothChecksPerSecond)
            : rotatingBothChecksPerSecond;

        if (searchChampion && !searchMercenaries)
            seconds = 1.0 / (shinyChance * championChecksPerSecond);
        else if (!searchChampion && searchMercenaries)
            seconds = 1.0 / (mercenaryChance * mercenaryChecksPerSecond);
        else if (!completeSelected)
            seconds = 1.0 / (anyThreeChance * bothChecksPerSecond);
        else
        {
            // 두 쪽을 모두 찾는 동안에는 먼저 발견된 쪽을 고정한다. 첫 발견까지의
            // 동시 탐색 시간과, 이후 남은 한쪽의 단독 탐색 시간을 함께 계산한다.
            double championOnlyFirst = shinyChance * (1.0 - mercenaryChance);
            double mercenaryOnlyFirst = (1.0 - shinyChance) * mercenaryChance;
            seconds = 1.0 / (anyThreeChance * bothChecksPerSecond);
            seconds += (championOnlyFirst / anyThreeChance) *
                (1.0 / (mercenaryChance * mercenaryChecksPerSecond));
            seconds += (mercenaryOnlyFirst / anyThreeChance) *
                (1.0 / (shinyChance * championChecksPerSecond));
        }

        return "현재 설정 기대시간: 평균 약 " + FormatExpectedDuration(seconds) +
            "\r\n샤이니 확률 1/5000 · 최근 실측/추정 속도 기준";
    }

    private static string FormatExpectedDuration(double seconds)
    {
        int totalSeconds = Math.Max(0, (int)Math.Round(seconds));
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int remainingSeconds = totalSeconds % 60;
        if (hours > 0) return string.Format("{0}시간 {1}분", hours, minutes);
        return string.Format("{0}분 {1:00}초", minutes, remainingSeconds);
    }

    private static void Start()
    {
        if (running) return;
        IntPtr hwnd = FindWindow(null, "Eslabong");
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("[{0:T}] Eslabong 창을 찾지 못했습니다.", DateTime.Now);
            return;
        }
        SetForegroundWindow(hwnd);
        Thread.Sleep(250);
        running = true;
        worker = new Thread(() => RerollLoop(hwnd));
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private static void Stop(string reason)
    {
        if (running) Console.WriteLine("[{0:T}] 중지: {1}", DateTime.Now, reason);
        running = false;
        if (reason.IndexOf("F10", StringComparison.OrdinalIgnoreCase) >= 0) SaveRecentFrames();
    }

    private static void RerollLoop(IntPtr hwnd)
    {
        if (Options.Mode == GameMode.Champion) ChampionRerollLoop(hwnd);
        else NormalRerollLoop(hwnd);
    }

    private static void NormalRerollLoop(IntPtr hwnd)
    {
        int rolls = 0;
        DateTime runStarted = DateTime.UtcNow;
        Console.WriteLine("[{0:T}] 시작했습니다.", DateTime.Now);
        Console.WriteLine("배포 모드: 매 리롤 화면은 저장하지 않습니다.");
        FrameSample pendingFrame = null;
        while (running && !exiting)
        {
            if (GetForegroundWindow() != hwnd)
            {
                Stop("게임 창이 전면이 아닙니다.");
                break;
            }

            using (FrameSample shot = pendingFrame ?? CapturePollingSample(hwnd))
            {
                pendingFrame = null;
                if (shot == null || shot.Image == null) { Stop("게임 화면 캡처에 실패했습니다."); break; }
                RememberRecentFrame(shot, rolls);
                if (StopIfShinySample(hwnd, shot, rolls, DateTime.UtcNow - runStarted)) return;

                // 완성된 화면을 한 프레임만 보고 넘기지 않는다. 애니메이션 자세나
                // 캡처 타이밍 때문에 한 프레임에서 표식이 흐려져도 다음 프레임에서 잡는다.
                Thread.Sleep(8);
                using (FrameSample secondFrame = CapturePollingSample(hwnd))
                {
                    if (secondFrame == null || secondFrame.Image == null) { Stop("두 번째 확인 화면 캡처에 실패했습니다."); break; }
                    if (StopIfShinySample(hwnd, secondFrame, rolls, DateTime.UtcNow - runStarted)) return;

                    byte[][] previousSlots = SplitThree(MakeScreenSignatureFromRegion(secondFrame.Image, secondFrame.ClientWidth, secondFrame.ClientHeight, secondFrame.Roi.X, secondFrame.Roi.Y));
                    byte[][] previousNames = SplitThree(MakeNameSignatureFromRegion(secondFrame.Image, secondFrame.ClientWidth, secondFrame.ClientHeight, secondFrame.Roi.X, secondFrame.Roi.Y));
                    PressGameKey(VkR);
                    rolls++;
                    pendingFrame = WaitForChangedFrame(hwnd, previousSlots, previousNames, new bool[] { true, true, true }, 2500, 120, 2);
                }
            }
            if (rolls % 25 == 0)
            {
                double seconds = Math.Max(0.001, (DateTime.UtcNow - runStarted).TotalSeconds);
                Console.WriteLine("[{0:T}] {1}회 확인 중... 평균 {2:F1}회/초", DateTime.Now, rolls, rolls / seconds);
            }
        }
        if (pendingFrame != null) pendingFrame.Dispose();
    }

    private static void ChampionRerollLoop(IntPtr hwnd)
    {
        int rolls = 0;
        int championRolls = 0;
        int mercenaryRolls = 0;
        bool championFound = !Options.SearchChampion;
        bool mercenaryFound = !Options.SearchMercenaries;
        FoundResult championResult = null;
        FoundResult mercenaryResult = null;
        FoundResult lastFound = null;
        DateTime runStarted = DateTime.UtcNow;
        FrameSample pendingFrame = null;
        Console.WriteLine("[{0:T}] 챔피언 모드 탐색을 시작했습니다.", DateTime.Now);

        while (running && !exiting)
        {
            if (GetForegroundWindow() != hwnd)
            {
                Stop("게임 창이 전면이 아닙니다.");
                break;
            }

            using (FrameSample shot = pendingFrame ?? CapturePollingSample(hwnd))
            {
                pendingFrame = null;
                if (shot == null || shot.Image == null) { Stop("게임 화면 캡처에 실패했습니다."); break; }
                RememberRecentFrame(shot, rolls);

                // 같은 완성 화면을 짧은 간격으로 두 번 검사한다. 챔피언의 상하 이동이나
                // 표식 애니메이션 때문에 한 프레임에서만 흐려지는 경우를 보완한다.
                for (int pass = 0; pass < 2 && running; pass++)
                {
                    FrameSample inspect = shot;
                    FrameSample second = null;
                    if (pass == 1)
                    {
                        Thread.Sleep(8);
                        second = CapturePollingSample(hwnd);
                        inspect = second;
                    }
                    try
                    {
                        if (inspect == null || inspect.Image == null) continue;
                        bool[] active = { !championFound, !mercenaryFound, !mercenaryFound };
                        FoundResult found;
                        while (TryFindShinySample(hwnd, inspect, rolls, DateTime.UtcNow - runStarted, active, out found))
                        {
                            lastFound = found;
                            bool isChampion = found.Slot == 1;
                            found.Rolls = isChampion ? championRolls : mercenaryRolls;
                            found.Elapsed = DateTime.UtcNow - runStarted;
                            if (isChampion) { championFound = true; championResult = found; }
                            else { mercenaryFound = true; mercenaryResult = found; }
                            string side = isChampion ? "챔피언" : "일반 용병";
                            Console.WriteLine("[{0:T}] {1} 샤이니 발견! 슬롯 {2}, 총 리롤 {3}회", DateTime.Now, side, found.Slot, rolls);
                            StartFoundAlert();

                            bool complete = championFound && mercenaryFound;
                            if (Options.StopMode == ChampionStopMode.FirstFound || complete)
                            {
                                running = false;
                                TimeSpan totalElapsed = DateTime.UtcNow - runStarted;
                                if (complete && Options.SearchChampion && Options.SearchMercenaries && championResult != null && mercenaryResult != null)
                                {
                                    using (Bitmap championPreview = CreateSlotPreview(championResult.FoundPath, championResult.Slot))
                                    using (Bitmap mercenaryPreview = CreateSlotPreview(mercenaryResult.FoundPath, mercenaryResult.Slot))
                                        ShowCombinedFoundPopup(championResult, mercenaryResult, rolls, totalElapsed, championPreview, mercenaryPreview);
                                }
                                else
                                {
                                    using (Bitmap preview = CreateSlotPreview(found.FoundPath, found.Slot))
                                        ShowFoundPopup(found.JobName, found.Slot, rolls, totalElapsed, preview);
                                }
                                return;
                            }

                            Console.WriteLine("[{0:T}] {1}은 고정하고, 아직 발견되지 않은 쪽만 계속 탐색합니다.", DateTime.Now, side);
                            active[0] = !championFound;
                            active[1] = active[2] = !mercenaryFound;
                        }
                    }
                    finally { if (second != null) second.Dispose(); }
                }

                bool rerollChampion = !championFound;
                bool rerollMercenaries = !mercenaryFound;
                if (!rerollChampion && !rerollMercenaries)
                {
                    running = false;
                    if (lastFound != null)
                    {
                        using (Bitmap preview = CreateSlotPreview(lastFound.FoundPath, lastFound.Slot))
                            ShowFoundPopup(lastFound.JobName, lastFound.Slot, rolls, DateTime.UtcNow - runStarted, preview);
                    }
                    return;
                }

                byte[][] currentSlots = SplitThree(MakeScreenSignatureFromRegion(shot.Image, shot.ClientWidth, shot.ClientHeight, shot.Roi.X, shot.Roi.Y));
                byte[][] currentNames = SplitThree(MakeNameSignatureFromRegion(shot.Image, shot.ClientWidth, shot.ClientHeight, shot.Roi.X, shot.Roi.Y));

                if (rerollChampion && Options.ChampionMode == ChampionRerollMode.FixedChampion)
                {
                    // 왼쪽으로 다른 챔피언을 한 번 불러온 뒤 오른쪽으로 돌아오면
                    // 원래 챔피언 종류는 유지하면서 샤이니 여부만 다시 추첨된다.
                    PressGameKey(VkLeft);
                    championRolls++;
                    using (FrameSample away = WaitForChangedFrame(hwnd, currentSlots, currentNames, new bool[] { true, false, false }, 2500, 70, 1))
                    {
                        if (away == null || !running) break;
                        byte[][] awaySlots = SplitThree(MakeScreenSignatureFromRegion(away.Image, away.ClientWidth, away.ClientHeight, away.Roi.X, away.Roi.Y));
                        byte[][] awayNames = SplitThree(MakeNameSignatureFromRegion(away.Image, away.ClientWidth, away.ClientHeight, away.Roi.X, away.Roi.Y));
                        PressGameKey(VkRight);
                        if (rerollMercenaries) { PressGameKey(VkR); mercenaryRolls++; }
                        int returnWaitMs = rerollMercenaries ? 90 : 70;
                        pendingFrame = WaitForChangedFrame(hwnd, awaySlots, awayNames,
                            new bool[] { true, rerollMercenaries, rerollMercenaries }, 2500, returnWaitMs, 1);
                    }
                }
                else
                {
                    if (rerollChampion) { PressGameKey(VkRight); championRolls++; }
                    if (rerollMercenaries) { PressGameKey(VkR); mercenaryRolls++; }
                    int championWaitMs = rerollChampion && rerollMercenaries ? 90 : (rerollChampion ? 70 : 80);
                    pendingFrame = WaitForChangedFrame(hwnd, currentSlots, currentNames,
                        new bool[] { rerollChampion, rerollMercenaries, rerollMercenaries }, 2500, championWaitMs, 1);
                }
                rolls++;
                if (pendingFrame == null && running) break;
            }

            if (rolls > 0 && rolls % 25 == 0)
            {
                double seconds = Math.Max(0.001, (DateTime.UtcNow - runStarted).TotalSeconds);
                Console.WriteLine("[{0:T}] 총 {1}회 확인 중... 챔피언 {2}회 / 용병 {3}회 / 평균 {4:F1}회/초",
                    DateTime.Now, rolls, championRolls, mercenaryRolls, rolls / seconds);
            }
        }
        if (pendingFrame != null) pendingFrame.Dispose();
    }

    private static bool TryFindShinySample(IntPtr hwnd, FrameSample sample, int rolls, TimeSpan elapsed, bool[] activeSlots, out FoundResult result)
    {
        result = null;
        Detection[] detections = AnalyzeRegionScreen(sample.Image, sample.ClientWidth, sample.ClientHeight, sample.Roi.X, sample.Roi.Y);
        for (int i = 0; i < detections.Length; i++)
        {
            if (!activeSlots[i] || !detections[i].Found) continue;
            if (!ConfirmCandidate(hwnd, i))
            {
                SaveCandidateEvidence(sample, rolls, i + 1, false);
                Console.WriteLine("[{0:T}] 순간 후보 제외: 슬롯 {1}, 리롤 {2}회 (후속 3프레임에서 지속되지 않음)", DateTime.Now, i + 1, rolls);
                continue;
            }

            SaveCandidateEvidence(sample, rolls, i + 1, true);
            string foundPath = null;
            using (Bitmap fullScreen = CaptureClient(hwnd))
            {
                if (fullScreen != null) foundPath = SaveFoundScreenshot(fullScreen, rolls, i + 1);
                else
                {
                    string foundDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "found");
                    Directory.CreateDirectory(foundDirectory);
                    foundPath = Path.Combine(foundDirectory, string.Format("shiny_crop_roll{0}_slot{1}.png", rolls, i + 1));
                    sample.Image.Save(foundPath, ImageFormat.Png);
                }
            }
            result = new FoundResult
            {
                Slot = i + 1,
                JobName = TryReadJobName(foundPath, i + 1),
                FoundPath = foundPath
            };
            return true;
        }
        return false;
    }

    private static void PressGameKey(uint key)
    {
        keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(14);
        keybd_event((byte)key, 0, KeyUp, UIntPtr.Zero);
    }

    private static void StartFoundAlert()
    {
        // 결과 팝업은 즉시 보여 주고, 알림음은 별도 스레드에서 약 5초간 재생한다.
        // 여러 슬롯을 같은 화면에서 연달아 발견해도 알림 스레드가 겹치지 않게 한다.
        if (Interlocked.CompareExchange(ref alertPlaying, 1, 0) != 0) return;
        Thread alert = new Thread(new ThreadStart(delegate
        {
            try
            {
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    System.Media.SystemSounds.Exclamation.Play();
                    Beep(950, 260);
                    Thread.Sleep(90);
                    Beep(1450, 320);
                    Thread.Sleep(90);
                    Beep(2050, 460);
                    if (repeat < 3) Thread.Sleep(180);
                }
            }
            finally { Interlocked.Exchange(ref alertPlaying, 0); }
        }));
        alert.IsBackground = true;
        alert.Start();
    }

    private static bool StopIfShinySample(IntPtr hwnd, FrameSample sample, int rolls, TimeSpan elapsed)
    {
        Detection[] detections = AnalyzeRegionScreen(sample.Image, sample.ClientWidth, sample.ClientHeight, sample.Roi.X, sample.Roi.Y);
        for (int i = 0; i < detections.Length; i++)
        {
            if (!detections[i].Found) continue;
            if (!ConfirmCandidate(hwnd, i))
            {
                SaveCandidateEvidence(sample, rolls, i + 1, false);
                Console.WriteLine("[{0:T}] 순간 후보 제외: 슬롯 {1}, 리롤 {2}회 (후속 3프레임에서 지속되지 않음)", DateTime.Now, i + 1, rolls);
                continue;
            }
            running = false;
            Console.WriteLine("[{0:T}] 샤이니 후보 발견! 슬롯 {1}, 리롤 {2}회, 점수 {3:F2}", DateTime.Now, i + 1, rolls, detections[i].Score);
            SaveCandidateEvidence(sample, rolls, i + 1, true);
            string foundPath = null;
            using (Bitmap fullScreen = CaptureClient(hwnd))
            {
                if (fullScreen != null) foundPath = SaveFoundScreenshot(fullScreen, rolls, i + 1);
                else
                {
                    string foundDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "found");
                    Directory.CreateDirectory(foundDirectory);
                    foundPath = Path.Combine(foundDirectory, string.Format("shiny_crop_roll{0}_slot{1}.png", rolls, i + 1));
                    sample.Image.Save(foundPath, ImageFormat.Png);
                }
            }
            string jobName = TryReadJobName(foundPath, i + 1);
            StartFoundAlert();
            using (Bitmap preview = CreateSlotPreview(foundPath, i + 1))
                ShowFoundPopup(jobName, i + 1, rolls, elapsed, preview);
            return true;
        }
        return false;
    }

    private static bool ConfirmCandidate(IntPtr hwnd, int slotIndex)
    {
        int confirmations = 0;
        for (int attempt = 0; attempt < 3 && running && !exiting; attempt++)
        {
            Thread.Sleep(45);
            using (FrameSample frame = CapturePollingSample(hwnd))
            {
                if (frame == null || frame.Image == null) continue;
                Detection[] next = AnalyzeRegionScreen(frame.Image, frame.ClientWidth, frame.ClientHeight, frame.Roi.X, frame.Roi.Y);
                if (next[slotIndex].Found) confirmations++;
            }
        }
        return confirmations >= 2;
    }

    private static void ShowFoundPopup(string jobName, int slot, int rolls, TimeSpan elapsed, Bitmap preview)
    {
        string elapsedText = FormatElapsed(elapsed);
        string headline = string.IsNullOrWhiteSpace(jobName)
            ? string.Format("샤이니 {0}번 슬롯 발견!", slot)
            : string.Format("샤이니 {0} 발견!", jobName.Trim());

        using (Form form = new Form())
        using (PictureBox picture = new PictureBox())
        using (Label label = new Label())
        using (Button button = new Button())
        {
            form.Text = "Eslabong 샤이니 발견";
            form.ClientSize = new Size(620, 700);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.TopMost = true;
            form.AutoScaleMode = AutoScaleMode.None;

            picture.Image = preview;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.BackColor = Color.FromArgb(29, 17, 12);
            picture.SetBounds(140, 20, 340, 330);

            label.Text = string.Format("{0}\r\n\r\n발견 슬롯: {1}번\r\n리롤 횟수: {2:N0}회\r\n소요 시간: {3}", headline, slot, rolls, elapsedText);
            label.Font = new Font("맑은 고딕", 15F, FontStyle.Regular);
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.SetBounds(25, 365, 570, 200);

            button.Text = "이제 겜하러 가기";
            button.Font = new Font("맑은 고딕", 12F, FontStyle.Bold);
            button.SetBounds(185, 615, 250, 55);
            button.DialogResult = DialogResult.OK;

            form.Controls.Add(picture);
            form.Controls.Add(label);
            form.Controls.Add(button);
            form.AcceptButton = button;
            form.Shown += delegate
            {
                // 전체 화면 게임이 직전에 전면 창이었더라도 결과창을 최상단에 고정하고
                // 키보드 포커스까지 결과창으로 가져온다.
                form.TopMost = true;
                SetWindowPos(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
                form.BringToFront();
                form.Activate();
                SetForegroundWindow(form.Handle);
            };
            form.ShowDialog();
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? string.Format("{0}시간 {1:D2}분 {2:D2}초", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds)
            : string.Format("{0}분 {1:D2}초", (int)elapsed.TotalMinutes, elapsed.Seconds);
    }

    private static void ShowCombinedFoundPopup(FoundResult champion, FoundResult mercenary, int totalRolls, TimeSpan totalElapsed, Bitmap championPreview, Bitmap mercenaryPreview)
    {
        string championName = string.IsNullOrWhiteSpace(champion.JobName) ? "챔피언" : champion.JobName.Trim();
        string mercenaryName = string.IsNullOrWhiteSpace(mercenary.JobName) ? "일반 용병" : mercenary.JobName.Trim();

        using (Form form = new Form())
        using (PictureBox championPicture = new PictureBox())
        using (PictureBox mercenaryPicture = new PictureBox())
        using (Label title = new Label())
        using (Label championStats = new Label())
        using (Label mercenaryStats = new Label())
        using (Label totalStats = new Label())
        using (Button button = new Button())
        {
            form.Text = "Eslabong 샤이니 2종 발견";
            form.ClientSize = new Size(1120, 820);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.TopMost = true;
            form.AutoScaleMode = AutoScaleMode.None;

            title.Text = "챔피언과 일반 용병 샤이니를 모두 발견했습니다!";
            title.Font = new Font("맑은 고딕", 18F, FontStyle.Bold);
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.SetBounds(50, 15, 1020, 55);

            championPicture.Image = championPreview;
            championPicture.SizeMode = PictureBoxSizeMode.Zoom;
            championPicture.BackColor = Color.FromArgb(29, 17, 12);
            championPicture.SetBounds(70, 85, 430, 330);

            mercenaryPicture.Image = mercenaryPreview;
            mercenaryPicture.SizeMode = PictureBoxSizeMode.Zoom;
            mercenaryPicture.BackColor = Color.FromArgb(29, 17, 12);
            mercenaryPicture.SetBounds(620, 85, 430, 330);

            championStats.Text = string.Format("샤이니 {0} 발견!\r\n구분: 챔피언\r\n리롤 횟수: {1:N0}회\r\n발견 시점: {2}",
                championName, champion.Rolls, FormatElapsed(champion.Elapsed));
            championStats.Font = new Font("맑은 고딕", 14F, FontStyle.Regular);
            championStats.TextAlign = ContentAlignment.MiddleCenter;
            championStats.SetBounds(45, 430, 480, 165);

            mercenaryStats.Text = string.Format("샤이니 {0} 발견!\r\n구분: 일반 용병 ({1}번 슬롯)\r\n리롤 횟수: {2:N0}회\r\n발견 시점: {3}",
                mercenaryName, mercenary.Slot, mercenary.Rolls, FormatElapsed(mercenary.Elapsed));
            mercenaryStats.Font = new Font("맑은 고딕", 14F, FontStyle.Regular);
            mercenaryStats.TextAlign = ContentAlignment.MiddleCenter;
            mercenaryStats.SetBounds(595, 430, 480, 165);

            totalStats.Text = string.Format("전체 통계  |  총 검사 횟수: {0:N0}회  |  전체 소요 시간: {1}", totalRolls, FormatElapsed(totalElapsed));
            totalStats.Font = new Font("맑은 고딕", 14F, FontStyle.Bold);
            totalStats.TextAlign = ContentAlignment.MiddleCenter;
            totalStats.SetBounds(100, 620, 920, 55);

            button.Text = "이제 겜하러 가기";
            button.Font = new Font("맑은 고딕", 12F, FontStyle.Bold);
            button.SetBounds(435, 720, 250, 58);
            button.DialogResult = DialogResult.OK;

            form.Controls.Add(title);
            form.Controls.Add(championPicture);
            form.Controls.Add(mercenaryPicture);
            form.Controls.Add(championStats);
            form.Controls.Add(mercenaryStats);
            form.Controls.Add(totalStats);
            form.Controls.Add(button);
            form.AcceptButton = button;
            form.Shown += delegate
            {
                form.TopMost = true;
                SetWindowPos(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
                form.BringToFront();
                form.Activate();
                SetForegroundWindow(form.Handle);
            };
            form.ShowDialog();
        }
    }

    private static Bitmap CreateSlotPreview(string fullScreenPath, int slot)
    {
        if (string.IsNullOrEmpty(fullScreenPath) || !File.Exists(fullScreenPath) || slot < 1 || slot > 3) return null;
        try
        {
            using (Bitmap full = new Bitmap(fullScreenPath))
            {
                if (full.Width < 1000 || full.Height < 560) return new Bitmap(full);
                double sx = full.Width / (double)RefWidth;
                double sy = full.Height / (double)RefHeight;
                int[] centers = { 1449, 1671, 1891 };
                Rectangle area = ScaleRect(centers[slot - 1] - 125, 620, 250, 270, sx, sy, full.Width, full.Height);
                return full.Clone(area, PixelFormat.Format24bppRgb);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("캐릭터 미리보기 생성 실패: " + ex.Message);
            return null;
        }
    }

    private static string TryReadJobName(string fullScreenPath, int slot)
    {
        if (string.IsNullOrEmpty(fullScreenPath) || !File.Exists(fullScreenPath)) return null;
        string tempCrop = Path.Combine(Path.GetTempPath(), "Eslabong_name_" + Guid.NewGuid().ToString("N") + ".png");
        string tempScript = Path.Combine(Path.GetTempPath(), "EslabongShinyOcr.ps1");
        try
        {
            using (Bitmap full = new Bitmap(fullScreenPath))
            {
                double sx = full.Width / (double)RefWidth;
                double sy = full.Height / (double)RefHeight;
                int[] centers = { 1449, 1671, 1891 };
                Rectangle nameArea = ScaleRect(centers[slot - 1] - 125, 795, 250, 90, sx, sy, full.Width, full.Height);
                using (Bitmap crop = full.Clone(nameArea, PixelFormat.Format24bppRgb))
                using (Bitmap enlarged = new Bitmap(crop.Width * 3, crop.Height * 3, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(enlarged))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(crop, 0, 0, enlarged.Width, enlarged.Height);
                    }
                    enlarged.Save(tempCrop, ImageFormat.Png);
                }
            }

            File.WriteAllText(tempScript, OcrPowerShellScript, new UTF8Encoding(true));
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = "powershell.exe";
            start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + tempScript + "\" \"" + tempCrop + "\"";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd().Trim();
                string error = process.StandardError.ReadToEnd().Trim();
                if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } return null; }
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    if (!string.IsNullOrWhiteSpace(error)) Console.WriteLine("직업명 OCR 실패: " + error.Split('\n')[0].Trim());
                    return null;
                }
                output = output.Replace("\r", " ").Replace("\n", " ").Trim();
                if (output == "팔라던") output = "팔라딘";
                if (output.Length > 24) return null;
                Console.WriteLine("직업명 OCR: " + output);
                return output;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("직업명 OCR 실패: " + ex.Message);
            return null;
        }
        finally
        {
            try { if (File.Exists(tempCrop)) File.Delete(tempCrop); } catch { }
        }
    }

    private const string OcrPowerShellScript = @"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
function Await-Operation($operation, [Type]$resultType) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and
            $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } | Select-Object -First 1
    $task = $method.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.Wait()
    return $task.Result
}
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]
$file = Await-Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($args[0])) ([Windows.Storage.StorageFile])
$stream = Await-Operation ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await-Operation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await-Operation ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('ko-KR'))
$result = Await-Operation ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$result.Text
";

    private static FrameSample WaitForChangedFrame(IntPtr hwnd, byte[][] previousSlots, byte[][] previousNames, bool[] requiredSlots, int timeoutMs, int minimumWaitMs = 150, int requiredReadyStreak = 2)
    {
        DateTime softDeadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        DateTime hardDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(6000, timeoutMs + 3500));
        Bitmap latest = null;
        bool[] sawLoading = { false, false, false };
        double[] maxSlotChange = { 0, 0, 0 };
        double[] maxNameChange = { 0, 0, 0 };
        int readyStreak = 0;
        DateTime waitStarted = DateTime.UtcNow;
        while (running && DateTime.UtcNow < hardDeadline)
        {
            Thread.Sleep(6);
            int clientWidth, clientHeight;
            Rectangle pollingRoi;
            Bitmap frame = CapturePollingFrame(hwnd, out clientWidth, out clientHeight, out pollingRoi);
            if (frame == null) continue;
            byte[][] currentSlots = SplitThree(MakeScreenSignatureFromRegion(frame, clientWidth, clientHeight, pollingRoi.X, pollingRoi.Y));
            byte[][] currentNames = SplitThree(MakeNameSignatureFromRegion(frame, clientWidth, clientHeight, pollingRoi.X, pollingRoi.Y));
            int[] brightCounts, requiredCounts;
            GetJobNameBrightnessFromRegion(frame, clientWidth, clientHeight, pollingRoi.X, pollingRoi.Y, out brightCounts, out requiredCounts);
            bool allNamesReady = true;
            bool allTransitioned = true;
            for (int i = 0; i < 3; i++)
            {
                if (!requiredSlots[i]) continue;
                if (brightCounts[i] < requiredCounts[i]) sawLoading[i] = true;
                if (brightCounts[i] < requiredCounts[i]) allNamesReady = false;
                maxSlotChange[i] = Math.Max(maxSlotChange[i], ChangedPixelFraction(previousSlots[i], currentSlots[i]));
                maxNameChange[i] = Math.Max(maxNameChange[i], SignatureDifference(previousNames[i], currentNames[i]));
                // 어두운 로딩 프레임을 놓쳐도 이름 변화나 애니메이션보다 큰 본체 변화로
                // 실제 리롤 전환을 확인한다.
                bool transitioned = sawLoading[i] || maxNameChange[i] >= 3.2 || maxSlotChange[i] >= 0.045;
                if (!transitioned) allTransitioned = false;
            }
            double waitedMs = (DateTime.UtcNow - waitStarted).TotalMilliseconds;
            // 모드별 최소 대기 뒤 완성된 직업명이 필요한 프레임 수만큼 확인되면
            // 즉시 진행한다. 늦은 로딩은 기존처럼 0.4초까지 기다린다.
            if (allTransitioned && allNamesReady && waitedMs >= minimumWaitMs) readyStreak++;
            else readyStreak = 0;
            // 0.4초가 지났더라도 직업명이 아직 어두운 로딩 상태면 절대 넘기지 않는다.
            // 특히 일반 모드 3번 슬롯이 늦게 뜨는 경우의 미탐을 막는다.
            bool turboReady = allNamesReady &&
                (readyStreak >= requiredReadyStreak || (allTransitioned && waitedMs >= 400));
            if (turboReady)
            {
                if (latest != null) latest.Dispose();
                return new FrameSample { Image = frame, ClientWidth = clientWidth, ClientHeight = clientHeight, Roi = pollingRoi };
            }
            // 수천 회 반복 중 드물게 어두운 로딩 프레임이나 변화 신호를 캡처하지
            // 못할 수 있다. 선택 슬롯의 직업명이 이미 모두 밝다면 완성 화면이므로,
            // 2.5초 뒤에는 정지하지 않고 이 화면으로 안전하게 복구한다.
            if (allNamesReady && DateTime.UtcNow >= softDeadline)
            {
                Console.WriteLine("[{0:T}] 화면 전환 신호를 놓쳤지만 선택 슬롯 로드는 완료되어 자동 복구합니다.", DateTime.Now);
                if (latest != null) latest.Dispose();
                return new FrameSample { Image = frame, ClientWidth = clientWidth, ClientHeight = clientHeight, Roi = pollingRoi };
            }
            if (latest != null) latest.Dispose();
            latest = frame;
        }
        if (latest != null) latest.Dispose();
        if (running) Stop("선택한 슬롯이 6초 안에도 로드되지 않아 안전 중지했습니다.");
        return null;
    }

    private static byte[][] SplitThree(byte[] combined)
    {
        int partLength = combined.Length / 3;
        byte[][] result = { new byte[partLength], new byte[partLength], new byte[combined.Length - partLength * 2] };
        Buffer.BlockCopy(combined, 0, result[0], 0, partLength);
        Buffer.BlockCopy(combined, partLength, result[1], 0, partLength);
        Buffer.BlockCopy(combined, partLength * 2, result[2], 0, result[2].Length);
        return result;
    }

    private static unsafe byte[] MakeNameSignature(Bitmap image)
    {
        return MakeNameSignatureFromRegion(image, image.Width, image.Height, 0, 0);
    }

    private static unsafe byte[] MakeNameSignatureFromRegion(Bitmap image, int clientWidth, int clientHeight, int originX, int originY)
    {
        double sx = clientWidth / (double)RefWidth;
        double sy = clientHeight / (double)RefHeight;
        int[] centers = { 1449, 1671, 1891 };
        List<byte> values = new List<byte>(4000);
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            byte* pixels = (byte*)data.Scan0.ToPointer();
            if (stride < 0) { pixels += (image.Height - 1) * (-stride); stride = -stride; }
            foreach (int center in centers)
            {
                Rectangle absolute = ScaleRect(center - 112, 812, 224, 62, sx, sy, clientWidth, clientHeight);
                Rectangle r = new Rectangle(absolute.X - originX, absolute.Y - originY, absolute.Width, absolute.Height);
                int stepX = Math.Max(2, r.Width / 56);
                int stepY = Math.Max(2, r.Height / 16);
                for (int y = r.Top; y < r.Bottom; y += stepY)
                for (int x = r.Left; x < r.Right; x += stepX)
                {
                    int offset = y * stride + x * 3;
                    values.Add(*(pixels + offset));
                    values.Add(*(pixels + offset + 1));
                    values.Add(*(pixels + offset + 2));
                }
            }
        }
        finally { image.UnlockBits(data); }
        return values.ToArray();
    }

    private static bool JobNamesReady(Bitmap image)
    {
        int[] counts;
        int[] required;
        GetJobNameBrightness(image, out counts, out required);
        for (int i = 0; i < 3; i++) if (counts[i] < required[i]) return false;
        return true;
    }

    private static bool JobNamesReadyRegion(Bitmap image, int clientWidth, int clientHeight, int originX, int originY)
    {
        int[] counts, required;
        GetJobNameBrightnessFromRegion(image, clientWidth, clientHeight, originX, originY, out counts, out required);
        for (int i = 0; i < 3; i++) if (counts[i] < required[i]) return false;
        return true;
    }

    private static unsafe void GetJobNameBrightness(Bitmap image, out int[] counts, out int[] requiredCounts)
    {
        GetJobNameBrightnessFromRegion(image, image.Width, image.Height, 0, 0, out counts, out requiredCounts);
    }

    private static unsafe void GetJobNameBrightnessFromRegion(Bitmap image, int clientWidth, int clientHeight, int originX, int originY, out int[] counts, out int[] requiredCounts)
    {
        double sx = clientWidth / (double)RefWidth;
        double sy = clientHeight / (double)RefHeight;
        int[] centers = { 1449, 1671, 1891 };
        counts = new int[3];
        requiredCounts = new int[3];
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            byte* pixels = (byte*)data.Scan0.ToPointer();
            if (stride < 0) { pixels += (image.Height - 1) * (-stride); stride = -stride; }
            for (int slot = 0; slot < centers.Length; slot++)
            {
                int center = centers[slot];
                Rectangle absolute = ScaleRect(center - 112, 812, 224, 62, sx, sy, clientWidth, clientHeight);
                Rectangle r = new Rectangle(absolute.X - originX, absolute.Y - originY, absolute.Width, absolute.Height);
                int brightGrayPixels = 0;
                for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    int offset = y * stride + x * 3;
                    int b = *(pixels + offset), g = *(pixels + offset + 1), red = *(pixels + offset + 2);
                    int max = Math.Max(red, Math.Max(g, b));
                    int min = Math.Min(red, Math.Min(g, b));
                    if (max >= 135 && min >= 95 && max - min <= 65) brightGrayPixels++;
                }
                int required = Math.Max(20, (int)(r.Width * r.Height * 0.0020));
                counts[slot] = brightGrayPixels;
                requiredCounts[slot] = required;
            }
        }
        finally { image.UnlockBits(data); }
    }

    private static int DiagnoseLive()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        IntPtr hwnd = FindWindow(null, "Eslabong");
        if (hwnd == IntPtr.Zero) { Console.WriteLine("Eslabong 창을 찾지 못했습니다."); return 2; }
        using (Bitmap image = CaptureClient(hwnd))
        {
            if (image == null) { Console.WriteLine("화면 캡처 실패"); return 2; }
            Console.WriteLine("capture={0}x{1}, scale={2:F3}x{3:F3}", image.Width, image.Height, image.Width / (double)RefWidth, image.Height / (double)RefHeight);
            int[] counts, required;
            GetJobNameBrightness(image, out counts, out required);
            Detection[] detections = AnalyzeLiveScreen(image);
            for (int i = 0; i < 3; i++)
            {
                string character = detections[i].Character == null ? "NOT_FOUND" :
                    string.Format("({0},{1})-({2},{3}) area={4}", detections[i].Character.Left, detections[i].Character.Top, detections[i].Character.Right, detections[i].Character.Bottom, detections[i].Character.Area);
                Console.WriteLine("slot{0}: name_bright={1}/{2}, character={3}", i + 1, counts[i], required[i], character);
            }
            return 0;
        }
    }

    private static unsafe byte[] MakeScreenSignature(Bitmap image)
    {
        return MakeScreenSignatureFromRegion(image, image.Width, image.Height, 0, 0);
    }

    private static unsafe byte[] MakeScreenSignatureFromRegion(Bitmap image, int clientWidth, int clientHeight, int originX, int originY)
    {
        double sx = clientWidth / (double)RefWidth;
        double sy = clientHeight / (double)RefHeight;
        int[] centers = { 1449, 1671, 1891 };
        List<byte> values = new List<byte>(900);
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            byte* pixels = (byte*)data.Scan0.ToPointer();
            if (stride < 0) { pixels += (image.Height - 1) * (-stride); stride = -stride; }
            foreach (int center in centers)
            {
                Rectangle absolute = ScaleRect(center - 115, 665, 230, 205, sx, sy, clientWidth, clientHeight);
                Rectangle r = new Rectangle(absolute.X - originX, absolute.Y - originY, absolute.Width, absolute.Height);
                int stepX = Math.Max(5, r.Width / 14);
                int stepY = Math.Max(5, r.Height / 13);
                for (int y = r.Top; y < r.Bottom; y += stepY)
                for (int x = r.Left; x < r.Right; x += stepX)
                {
                    int offset = y * stride + x * 3;
                    values.Add(*(pixels + offset));
                    values.Add(*(pixels + offset + 1));
                    values.Add(*(pixels + offset + 2));
                }
            }
        }
        finally { image.UnlockBits(data); }
        return values.ToArray();
    }

    private static double SignatureDifference(byte[] a, byte[] b)
    {
        int count = Math.Min(a.Length, b.Length);
        if (count == 0) return 255;
        long sum = 0;
        for (int i = 0; i < count; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)count;
    }

    private static double ChangedPixelFraction(byte[] a, byte[] b)
    {
        int count = Math.Min(a.Length, b.Length) / 3;
        if (count == 0) return 1.0;
        int changed = 0;
        for (int pixel = 0; pixel < count; pixel++)
        {
            int i = pixel * 3;
            int colorDistance = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            if (colorDistance >= 55) changed++;
        }
        return changed / (double)count;
    }

    private static Bitmap CaptureClient(IntPtr hwnd)
    {
        Rect rect;
        if (!GetClientRect(hwnd, out rect)) return null;
        Point p = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref p)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 800 || height < 450) return null;
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static FrameSample CapturePollingSample(IntPtr hwnd)
    {
        int clientWidth, clientHeight;
        Rectangle roi;
        Bitmap image = CapturePollingFrame(hwnd, out clientWidth, out clientHeight, out roi);
        if (image == null) return null;
        return new FrameSample { Image = image, ClientWidth = clientWidth, ClientHeight = clientHeight, Roi = roi };
    }

    private static void RememberRecentFrame(FrameSample sample, int roll)
    {
        Bitmap crop = sample.Image.Clone(new Rectangle(0, 0, sample.Image.Width, sample.Image.Height), PixelFormat.Format24bppRgb);
        lock (RecentLock)
        {
            RecentFrames.Enqueue(new RecentFrame { Image = crop, Roll = roll });
            while (RecentFrames.Count > 12)
            {
                RecentFrame old = RecentFrames.Dequeue();
                old.Image.Dispose();
            }
        }
    }

    private static void SaveRecentFrames()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_recent");
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        int saved = 0;
        lock (RecentLock)
        {
            while (RecentFrames.Count > 0)
            {
                RecentFrame frame = RecentFrames.Dequeue();
                string path = Path.Combine(directory, string.Format("recent_{0}_{1:D2}_roll{2}.png", stamp, saved, frame.Roll));
                frame.Image.Save(path, ImageFormat.Png);
                frame.Image.Dispose();
                saved++;
            }
        }
        if (saved > 0) Console.WriteLine("최근 {0}회 화면 저장: {1}", saved, directory);
    }

    private static Bitmap CapturePollingFrame(IntPtr hwnd, out int clientWidth, out int clientHeight, out Rectangle roi)
    {
        clientWidth = 0;
        clientHeight = 0;
        roi = Rectangle.Empty;
        Rect rect;
        if (!GetClientRect(hwnd, out rect)) return null;
        Point p = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref p)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 800 || height < 450) return null;
        clientWidth = width;
        clientHeight = height;

        // 캐릭터 외곽선, 샤이니 표식, 직업명까지 포함하는 최소 영역.
        // Bitmap 좌표계는 전체 클라이언트와 같게 유지해 기존 1440p/4K 보정을 그대로 쓴다.
        double sx = width / (double)RefWidth;
        double sy = height / (double)RefHeight;
        roi = ScaleRect(1315, 635, 715, 250, sx, sy, width, height);
        Bitmap bitmap = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(p.X + roi.X, p.Y + roi.Y, 0, 0, roi.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Detection[] AnalyzeLiveScreen(Bitmap image)
    {
        double sx = image.Width / (double)RefWidth;
        double sy = image.Height / (double)RefHeight;
        int[] centers = { 1449, 1671, 1891 };
        Detection[] result = new Detection[3];
        for (int i = 0; i < 3; i++)
        {
            Rectangle crop = ScaleRect(centers[i] - 125, 645, 250, 175, sx, sy, image.Width, image.Height);
            using (Bitmap part = image.Clone(crop, PixelFormat.Format24bppRgb))
                result[i] = DetectMarker(part, false);
        }
        return result;
    }

    private static Detection[] AnalyzeRegionScreen(Bitmap region, int clientWidth, int clientHeight, int originX, int originY)
    {
        double sx = clientWidth / (double)RefWidth;
        double sy = clientHeight / (double)RefHeight;
        int[] centers = { 1449, 1671, 1891 };
        Detection[] result = new Detection[3];
        for (int i = 0; i < 3; i++)
        {
            Rectangle absolute = ScaleRect(centers[i] - 125, 645, 250, 175, sx, sy, clientWidth, clientHeight);
            Rectangle local = new Rectangle(absolute.X - originX, absolute.Y - originY, absolute.Width, absolute.Height);
            local.Intersect(new Rectangle(0, 0, region.Width, region.Height));
            if (local.Width <= 0 || local.Height <= 0)
            {
                result[i] = new Detection { Reason = "판정 영역 밖" };
                continue;
            }
            using (Bitmap part = region.Clone(local, PixelFormat.Format24bppRgb))
                result[i] = DetectMarker(part, false);
        }
        return result;
    }

    private static Rectangle ScaleRect(int x, int y, int w, int h, double sx, double sy, int maxW, int maxH)
    {
        int rx = Math.Max(0, (int)Math.Round(x * sx));
        int ry = Math.Max(0, (int)Math.Round(y * sy));
        int rw = Math.Min(maxW - rx, Math.Max(1, (int)Math.Round(w * sx)));
        int rh = Math.Min(maxH - ry, Math.Max(1, (int)Math.Round(h * sy)));
        return new Rectangle(rx, ry, rw, rh);
    }

    private static Detection DetectMarker(Bitmap image, bool standaloneExample)
    {
        bool[,] mask = MakeForegroundMask(image, standaloneExample);
        Dilate(mask, 1);
        List<Blob> blobs = Components(mask, 7);
        Blob character = null;
        double bestCharacter = double.MinValue;

        foreach (Blob b in blobs)
        {
            if (b.Height < Math.Max(18, image.Height * 0.16) || b.Area < 45) continue;
            double centerBias = 1.0 - Math.Min(1.0, Math.Abs(b.CenterX - image.Width * 0.55) / (image.Width * 0.55));
            double lowerBias = b.CenterY / image.Height;
            double score = b.Area * (0.65 + centerBias) * (0.7 + lowerBias);
            if (score > bestCharacter) { bestCharacter = score; character = b; }
        }

        Detection detection = new Detection { Character = character, Reason = "캐릭터 외곽선을 찾지 못함" };
        if (character == null) return detection;

        Blob best = null;
        double bestScore = 0;
        double bestBrightColorFraction = 0;
        double h = character.Height;
        foreach (Blob b in blobs)
        {
            if (ReferenceEquals(b, character)) continue;
            double sizeRatio = Math.Max(b.Width, b.Height) / h;
            double aspect = b.Width / (double)b.Height;
            double leftDistance = (character.Left - b.CenterX) / h;
            double vertical = (b.CenterY - character.Top) / h;
            double areaRatio = b.Area / (double)Math.Max(1, character.Area);
            int gapX = character.Left - b.Right;

            // 샤이니 UI 표식은 캐릭터 크기와 달리 화면 해상도에 비례한 일정 크기다.
            // 4K에서 13x13에 불과했던 불꽃/방패 장식은 제외하고 다양한 큰 표식은 유지한다.
            if (Math.Max(b.Width, b.Height) < image.Width * 0.055) continue;
            // 10px 안팎의 분리된 머리/어깨 장식까지 표식으로 오인하지 않는다.
            // 실제 샤이니 예시는 캐릭터 높이 대비 0.32, 새 오탐 장식은 0.17이었다.
            // 표식 자체의 화면 크기는 비슷하지만 캐릭터는 직업에 따라 매우 작을 수 있다.
            // 작은 캐릭터에서는 같은 표식이 본체 높이의 0.6배를 넘으므로 상한을 넓힌다.
            if (sizeRatio < 0.19 || sizeRatio > 1.00) continue;
            if (aspect < 0.32 || aspect > 3.10) continue;
            // 록스카처럼 왼쪽 장비/자루가 본체 외곽선을 넓히면 별이 외곽선의
            // 가로 범위 안에 들어온다. 머리 기준 좌측 상단이므로 제한적인 겹침은 허용한다.
            if (leftDistance < -0.40 || leftDistance > 1.35) continue;
            // 표식은 머리의 좌측 상단에 뜬다. 몸통 높이까지 내려온 분리 조각은
            // 팔/무기/장식이다. 실제 오탐(세뇌 주술사 팔)은 vertical=0.52였다.
            if (vertical < -0.32 || vertical > 0.45) continue;
            // 일반 캐릭터에서는 눈에 보이는 가로 틈이 있지만, 왼쪽으로 큰 장비를 든
            // 캐릭터는 표식과 전체 외곽선의 x 범위가 겹칠 수 있다.
            if (gapX < -h * 0.40 || gapX > h * 1.15) continue;
            // 겹침 예외는 사용자가 확인한 동일한 별/태양형(거의 정사각형)에만 적용한다.
            // 세로로 긴 팔·무기 조각은 여기서 제외한다.
            int normalGap = Math.Max(2, (int)Math.Ceiling(h * 0.025));
            bool overlapsWideCharacter = gapX < normalGap;
            double brightColorFraction = BrightColorFraction(image, b);
            if (overlapsWideCharacter && (aspect < 0.70 || aspect > 1.45)) continue;
            // 실제 보라 방패형 표식은 밝은 색 비율이 20.2%로 낮지만 본체와 6px
            // 분리되어 있다. 주술사 머리 조각은 19.8%이고 본체 안쪽으로 겹친다.
            // 따라서 어두운 표식 전체를 버리지 않고, 본체와 겹치는 후보만 더 밝게 요구한다.
            if (overlapsWideCharacter && brightColorFraction < 0.22) continue;
            // 암흑기사 방패는 애니메이션에 따라 본체와 붙기도, 작은 틈을 두고 떨어지기도 한다.
            // 위치와 무관하게 한 픽셀 색이 아닌 후보 도형 전체의 밝은 색상 비율을 검사한다.
            if (brightColorFraction < 0.08) continue;
            // 샤이니 표식은 확인된 정상 사례에서 본체 면적의 약 7~9%였다.
            // 고블린 암살자의 분리된 머리카락은 작은 몸통 조각의 58%로 잡혔으므로,
            // 색상이나 도형 종류를 제한하지 않고 상대 면적이 지나치게 큰 후보만 제외한다.
            if (areaRatio < 0.045 || areaRatio > 0.38) continue;

            double compactness = b.Area / (double)(b.Width * b.Height);
            double score = 1.0;
            // 작은 본체 때문에 비율이 커져도 샤이니 점수를 과도하게 깎지 않는다.
            score -= Math.Min(0.18, Math.Abs(sizeRatio - 0.32) * 0.25);
            score -= Math.Min(0.18, Math.Abs(leftDistance - 0.16) * 0.22);
            score -= Math.Min(0.20, Math.Abs(vertical - 0.25) * 0.30);
            score += Math.Min(0.20, compactness * 0.22);
            if (score > bestScore) { bestScore = score; best = b; bestBrightColorFraction = brightColorFraction; }
        }

        detection.Marker = best;
        detection.Score = bestScore;
        detection.BrightColorFraction = bestBrightColorFraction;
        detection.Found = best != null && bestScore >= 0.64;
        detection.Reason = detection.Found ? "분리된 좌측 상단 표식" : "조건에 맞는 분리 표식 없음";
        return detection;
    }

    private static double BrightColorFraction(Bitmap image, Blob blob)
    {
        int brightColored = 0;
        int total = 0;
        int left = Math.Max(0, blob.Left), right = Math.Min(image.Width - 1, blob.Right);
        int top = Math.Max(0, blob.Top), bottom = Math.Min(image.Height - 1, blob.Bottom);
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            Color c = image.GetPixel(x, y);
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            if (max >= 150 && max - min >= 32) brightColored++;
            total++;
        }
        return total == 0 ? 0 : brightColored / (double)total;
    }

    private static bool[,] MakeForegroundMask(Bitmap image, bool standaloneExample)
    {
        int w = image.Width, h = image.Height;
        BitmapData data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int stride = Math.Abs(data.Stride);
        byte[] pixels = new byte[stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        image.UnlockBits(data);
        Color background = EstimateBackground(pixels, stride, w, h, standaloneExample);
        bool[,] mask = new bool[w, h];
        int maxY = standaloneExample ? Math.Min(h, (int)(h * 0.56)) : h;
        for (int y = 0; y < maxY; y++)
        for (int x = 0; x < w; x++)
        {
            int offset = y * stride + x * 3;
            int blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
            int dr = red - background.R, dg = green - background.G, db = blue - background.B;
            int distance = Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db);
            int brightness = Math.Max(red, Math.Max(green, blue));
            int spread = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            mask[x, y] = distance >= 52 && (brightness >= 46 || spread >= 24);
        }
        return mask;
    }

    private static Color EstimateBackground(byte[] pixels, int stride, int w, int h, bool standaloneExample)
    {
        List<int> rs = new List<int>(), gs = new List<int>(), bs = new List<int>();
        int limitY = standaloneExample ? Math.Min(h, (int)(h * 0.55)) : h;
        int step = Math.Max(2, Math.Min(w, limitY) / 80);
        for (int y = 0; y < limitY; y += step)
        for (int x = 0; x < w; x += step)
        {
            bool border = x < w * 0.18 || x > w * 0.82 || y < limitY * 0.15;
            if (!border) continue;
            int offset = y * stride + x * 3;
            bs.Add(pixels[offset]); gs.Add(pixels[offset + 1]); rs.Add(pixels[offset + 2]);
        }
        rs.Sort(); gs.Sort(); bs.Sort();
        int mid = rs.Count / 2;
        return Color.FromArgb(rs[mid], gs[mid], bs[mid]);
    }

    private static void Dilate(bool[,] mask, int radius)
    {
        int w = mask.GetLength(0), h = mask.GetLength(1);
        bool[,] source = (bool[,])mask.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!source[x, y]) continue;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h) mask[nx, ny] = true;
            }
        }
    }

    private static List<Blob> Components(bool[,] mask, int minArea)
    {
        int w = mask.GetLength(0), h = mask.GetLength(1);
        bool[,] seen = new bool[w, h];
        List<Blob> blobs = new List<Blob>();
        int[] qx = new int[w * h], qy = new int[w * h];
        int[] dd = { -1, 0, 1 };
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!mask[x, y] || seen[x, y]) continue;
            int head = 0, tail = 0;
            qx[tail] = x; qy[tail++] = y; seen[x, y] = true;
            Blob b = new Blob();
            while (head < tail)
            {
                int px = qx[head], py = qy[head++];
                b.Area++; b.Left = Math.Min(b.Left, px); b.Right = Math.Max(b.Right, px); b.Top = Math.Min(b.Top, py); b.Bottom = Math.Max(b.Bottom, py);
                foreach (int dy in dd) foreach (int dx in dd)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = px + dx, ny = py + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && mask[nx, ny] && !seen[nx, ny])
                    { seen[nx, ny] = true; qx[tail] = nx; qy[tail++] = ny; }
                }
            }
            if (b.Area >= minArea) blobs.Add(b);
        }
        return blobs;
    }

    private static int AnalyzeFile(string path)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!File.Exists(path)) { Console.WriteLine("파일이 없습니다: " + path); return 2; }
        using (Bitmap image = new Bitmap(path))
        {
            Detection d = DetectMarker(image, true);
            Console.WriteLine("result={0}", d.Found ? "SHINY_MARKER" : "NOT_FOUND");
            Console.WriteLine("reason={0}", d.Reason);
            if (d.Character != null)
                Console.WriteLine("character=({0},{1})-({2},{3}), area={4}", d.Character.Left, d.Character.Top, d.Character.Right, d.Character.Bottom, d.Character.Area);
            if (d.Marker != null)
                Console.WriteLine("marker=({0},{1})-({2},{3}), area={4}, score={5:F3}, bright_color={6:F3}", d.Marker.Left, d.Marker.Top, d.Marker.Right, d.Marker.Bottom, d.Marker.Area, d.Score, d.BrightColorFraction);
            return d.Found ? 0 : 1;
        }
    }

    private static int AnalyzeScreenFile(string path)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!File.Exists(path)) { Console.WriteLine("파일이 없습니다: " + path); return 2; }
        using (Bitmap image = new Bitmap(path))
        {
            Console.WriteLine("screen={0}x{1}", image.Width, image.Height);
            Detection[] detections = AnalyzeLiveScreen(image);
            for (int i = 0; i < detections.Length; i++)
            {
                Detection d = detections[i];
                string character = d.Character == null ? "none" : string.Format("({0},{1})-({2},{3}) area={4}", d.Character.Left, d.Character.Top, d.Character.Right, d.Character.Bottom, d.Character.Area);
                string marker = d.Marker == null ? "none" : string.Format("({0},{1})-({2},{3}) area={4}", d.Marker.Left, d.Marker.Top, d.Marker.Right, d.Marker.Bottom, d.Marker.Area);
                Console.WriteLine("slot{0}: found={1}, score={2:F3}, bright_color={3:F3}, character={4}, marker={5}", i + 1, d.Found, d.Score, d.BrightColorFraction, character, marker);
            }
            return 0;
        }
    }

    private static int AnalyzeRegionFile(string path, int clientWidth, int clientHeight, int originX, int originY)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!File.Exists(path)) { Console.WriteLine("파일이 없습니다: " + path); return 2; }
        using (Bitmap image = new Bitmap(path))
        {
            Detection[] detections = AnalyzeRegionScreen(image, clientWidth, clientHeight, originX, originY);
            bool any = false;
            for (int i = 0; i < detections.Length; i++)
            {
                Detection d = detections[i];
                any |= d.Found;
                Console.WriteLine("slot{0}: found={1}, score={2:F3}, bright_color={3:F3}", i + 1, d.Found, d.Score, d.BrightColorFraction);
            }
            return any ? 0 : 1;
        }
    }

    private static void SaveCandidateEvidence(FrameSample sample, int rolls, int slot, bool confirmed)
    {
        try
        {
            string folder = confirmed ? "found" : "rejected_candidates";
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder);
            Directory.CreateDirectory(dir);
            string name = string.Format("{0}_{1:yyyyMMdd_HHmmss_fff}_roll{2}_slot{3}.png", confirmed ? "candidate" : "rejected", DateTime.Now, rolls, slot);
            sample.Image.Save(Path.Combine(dir, name), ImageFormat.Png);
        }
        catch (Exception ex) { Console.WriteLine("후보 증거 화면 저장 실패: " + ex.Message); }
    }

    private static string SaveFoundScreenshot(Bitmap image, int rolls, int slot)
    {
        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "found");
            Directory.CreateDirectory(dir);
            string name = string.Format("shiny_{0:yyyyMMdd_HHmmss}_roll{1}_slot{2}.png", DateTime.Now, rolls, slot);
            string path = Path.Combine(dir, name);
            image.Save(path, ImageFormat.Png);
            Console.WriteLine("확인용 화면 저장: " + path);
            return path;
        }
        catch (Exception ex) { Console.WriteLine("화면 저장 실패: " + ex.Message); return null; }
    }
}

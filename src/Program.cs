// CoreDotSetup.exe = CoreDotManager.exe (같은 파일 하나)
//   받은 자리에서 실행     → 관리자 확인 → 설치 (Program Files\CoreDot\CoreDotManager.exe 로 자기 자신을 복사)
//   설치된 자리에서 실행   → 관리센터 창
//   --check               → 새 버전 · 새 프로그램이 있으면 알림 창 (작업 스케줄러: 로그인 시 · 3시간마다)
//   --service             → 백그라운드 설치 (작업 스케줄러 \CoreDot\Installer, SYSTEM)
//   --setup [/silent]     → 설치 (관리자)          --uninstall → 제거
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CoreDot
{
    static class Program
    {
        public const string Version = "1.0.4";

        [STAThread]
        static int Main(string[] args)
        {
            var a = new List<string>();
            foreach (var x in args) a.Add(x.ToLowerInvariant());
            bool silent = a.Contains("/silent") || a.Contains("--silent");

            CleanupOld();

            if (a.Contains("--service")) return Service.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (a.Contains("--uninstall")) return SetupMode.Uninstall();
            if (a.Contains("--setup"))
            {
                if (!SetupMode.IsAdmin()) return SetupMode.Elevate("--setup" + (silent ? " /silent" : ""));
                return SetupMode.Run(silent);
            }
            if (a.Contains("--check")) return Check();

            bool installed = string.Equals(SetupMode.SelfPath(), Path.GetFullPath(Paths.ManagerExe), StringComparison.OrdinalIgnoreCase);
            if (!installed)
            {
                if (!SetupMode.IsAdmin()) return SetupMode.Elevate("--setup" + (silent ? " /silent" : ""));
                return SetupMode.Run(silent);
            }
            return Hub();
        }

        static void CleanupOld()
        {
            try { foreach (var f in Directory.GetFiles(Paths.ProgramDir, "*.old")) { try { File.Delete(f); } catch { } } }
            catch { }
        }

        static int Hub()
        {
            Core.LogFile = Paths.UserLog;
            bool created;
            using (var m = new Mutex(true, @"Local\CoreDotManagerHub", out created))
            {
                if (!created) return 0;
                Application.Run(new HubForm());
            }
            return 0;
        }

        static int Check()
        {
            Core.LogFile = Paths.UserLog;
            bool created;
            using (var m = new Mutex(true, @"Local\CoreDotManagerCheck", out created))
            {
                if (!created) return 0;
                try
                {
                    var cat = Catalog.Fetch();
                    if (cat.FromCache) return 0;   // 인터넷이 안 되면 조용히
                    ReleaseInfo printer = null;
                    try { printer = Core.GetLatestPrinter(); } catch (Exception e) { Core.Log("코어닷프린터 확인 실패: " + e.Message); }
                    var pending = Catalog.ComputePending(cat, printer, Version);
                    Core.Log("확인: " + pending.Count + "개");
                    if (pending.Count == 0) return 0;
                    using (var p = new PopupForm(pending))
                    {
                        if (p.ShowDialog() != DialogResult.OK) { Core.Log("사용자가 '나중에'"); return 0; }
                        using (var f = new ProgressForm(p.Chosen, printer)) f.ShowDialog();
                    }
                }
                catch (Exception e) { Core.Log("확인 실패: " + e); return 1; }
            }
            return 0;
        }
    }
}

// CoreDotSetup.exe = CoreDotManager.exe (같은 파일 하나)
//   받은 자리에서 실행     → 관리자 확인 → 설치 (Program Files\CoreDot\CoreDotManager.exe 로 자기 자신을 복사)
//   설치된 자리에서 실행   → 관리센터 창
//   --check               → 깔린 것(관리센터 포함)만 묻지 않고 자동 업데이트 (작업 스케줄러: 로그인 시 · 3시간마다)
//   --service             → 백그라운드 설치 (작업 스케줄러 \CoreDot\Installer, SYSTEM)
//   --setup [/silent] [/items:id,id,…]  → 설치 (관리자). 창에서는 고르고, /silent 는 /items 에 적은 것만 (없으면 전부)
//   --uninstall           → 제거
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CoreDot
{
    static class Program
    {
        public const string Version = "1.0.6";

        [STAThread]
        static int Main(string[] args)
        {
            var a = new List<string>();
            foreach (var x in args) a.Add(x.ToLowerInvariant());
            bool silent = a.Contains("/silent") || a.Contains("--silent");
            string itemsArg = a.Find(delegate(string x) { return x.StartsWith("/items:"); });
            HashSet<string> only = null;
            if (itemsArg != null)
            {
                only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in itemsArg.Substring(7).Split(',')) if (id.Trim().Length > 0) only.Add(id.Trim());
            }
            string setupArgs = "--setup" + (silent ? " /silent" : "") + (itemsArg != null ? " " + itemsArg : "");

            CleanupOld();

            if (a.Contains("--service")) return Service.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (a.Contains("--uninstall")) return SetupMode.Uninstall();
            if (a.Contains("--setup"))
            {
                if (!SetupMode.IsAdmin()) return SetupMode.Elevate(setupArgs);
                return SetupMode.Run(silent, only);
            }
            if (a.Contains("--check")) return Check();

            bool installed = string.Equals(SetupMode.SelfPath(), Path.GetFullPath(Paths.ManagerExe), StringComparison.OrdinalIgnoreCase);
            if (!installed)
            {
                if (!SetupMode.IsAdmin()) return SetupMode.Elevate(setupArgs);
                return SetupMode.Run(silent, only);
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
                    // 깔려 있는 것만 본다 (안 깐 것 · 새로 추가된 것은 알리지 않는다 — 관리센터 창에서 [설치] 하면 그때부터 대상).
                    // 항목마다 고른 방식: 자동 업데이트 → 묻지 않고 올림 · 알림만 → 알림 창 · 업데이트 안 함 → 그대로 둠.
                    // 관리센터 자신은 늘 자동. 깔린 프로그램에 필요한데 빠진 것(requires)도 묻지 않고 채운다.
                    var all = Catalog.ComputePending(cat, printer, Version);
                    var prefs = Prefs.Load();
                    var auto = new List<Catalog.Pending>();
                    var notify = new List<Catalog.Pending>();
                    int skipped = 0;
                    foreach (var p in all)
                    {
                        if (p.IsNew) continue;
                        string mode;
                        if (p.Id == "manager" || !prefs.TryGetValue(p.Id, out mode)) mode = Prefs.Auto;
                        if (mode == Prefs.Auto) auto.Add(p);
                        else if (mode == Prefs.Notify) notify.Add(p);
                        else skipped++;
                    }
                    foreach (var need in Catalog.MissingRequires(cat)) { Core.Log("빠진 필수 구성: " + need.Title); auto.Add(need); }
                    var tries = AutoTries.Load();
                    int resting = auto.RemoveAll(delegate(Catalog.Pending p) { return AutoTries.Resting(tries, p.Id, p.To); });
                    if (resting > 0) Core.Log("같은 판이 여러 번 실패해 쉬는 항목 " + resting + "개 (하루 뒤 다시)");
                    Core.Log("확인: 자동 " + auto.Count + " · 알림 " + notify.Count + " · 업데이트 안 함 " + skipped + " · 안 깐 것 " + (all.Count - auto.Count - notify.Count - skipped) + "개");

                    int fails = 0;
                    if (auto.Count > 0)
                    {
                        int printing = Catalog.PrintJobs();
                        if (printing > 0) Core.Log("인쇄 중 (" + printing + "건) — 자동 업데이트는 다음 확인 때");
                        else
                        {
                            foreach (var j in auto) Core.Log("자동 업데이트: " + j.Title + " " + (j.From ?? "(없음)") + " → " + j.To);
                            var r = Updater.Install(auto, printer, null, false, cat);
                            fails = r.Fails;
                            Core.Log("자동 업데이트 끝: 실패 " + r.Fails + "개" + (r.Reboot ? " · 재부팅 필요" : ""));
                            // 실제로 목표 판이 됐는지로 성공 · 실패를 적는다 (설치는 '성공' 인데 판이 그대로인 경우도 실패로)
                            foreach (var j in auto)
                            {
                                bool ok;
                                if (j.Id == "manager") ok = r.ManagerUpdated;
                                else if (j.Id == "coredotprinter") ok = printer != null && Core.IsUpToDate(printer);
                                else
                                {
                                    var it = cat.Find(j.Id);
                                    string have = it == null ? null : Catalog.InstalledVersion(it);
                                    ok = have != null && (have == "설치됨" || Core.CompareVersions(have, it.Version) >= 0);
                                }
                                AutoTries.Record(tries, j.Id, j.To, ok);
                            }
                            AutoTries.Save(tries);
                        }
                    }
                    else if (Engine.AutostartMissing(cat) && !AutoTries.Resting(tries, "ensure", Version))
                    {
                        // 자동 실행 · SysWOW64 사본이 빠진 PC (예: 그 기능이 생기기 전에 깔린 IDP) — 서비스가 맞추고, 닫힌 것은 다시 켠다
                        Core.Log("자동 실행 · 32비트 사본 맞추기 요청");
                        bool ok = Updater.Ensure(cat) && !Engine.AutostartMissing(cat);
                        AutoTries.Record(tries, "ensure", Version, ok);
                        AutoTries.Save(tries);
                    }

                    if (notify.Count > 0 && !NotifyOnce.Due())
                        Core.Log("알림 " + notify.Count + "개 — 오늘 이미 알렸으므로 내일 다시");
                    else if (notify.Count > 0)
                    {
                        NotifyOnce.Mark();   // 하루에 한 번만 (나중에 를 눌러도)
                        using (var pf = new PopupForm(notify))
                        {
                            if (pf.ShowDialog() != DialogResult.OK) { Core.Log("사용자가 '나중에'"); return fails == 0 ? 0 : 1; }
                            using (var f = new ProgressForm(pf.Chosen, printer, cat)) f.ShowDialog();
                        }
                    }
                    return fails == 0 ? 0 : 1;
                }
                catch (Exception e) { Core.Log("확인 실패: " + e); return 1; }
            }
        }
    }
}

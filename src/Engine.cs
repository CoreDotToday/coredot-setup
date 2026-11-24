// 설치 엔진 (관리자 · SYSTEM 권한에서만 돈다) + 백그라운드 설치 서비스 + 사용자 쪽에서 서비스 부르기
//
// 권한 구조:
//  - 사용자(관리센터 창 · 알림)는 '이 항목들 설치해 줘' 를 ProgramData\CoreDot\requests 에 id 만 적고 작업 스케줄러의
//    \CoreDot\Installer (SYSTEM) 를 부른다. 관리자 확인 창 없음.
//  - 서비스는 목록(catalog.json)을 직접 받아 그 안의 항목만, SHA256 이 맞을 때만 설치한다 — 사용자가 적은 것은 id 뿐이라
//    임의 파일을 실행시킬 수 없다. 받은 파일은 SYSTEM · 관리자만 쓸 수 있는 cache 에 둔다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace CoreDot
{
    public class Engine
    {
        public Action<string, string, string> Report;   // (id, state, message) — state: downloading/installing/done/error/skipped
        public bool NeedReboot;
        public bool SkipIfCurrent;   // 자동 업데이트 요청: 이미 그 판 이상이면 다시 깔지 않는다 (프로그램을 닫고 다시 까는 일 없게)
        readonly HashSet<string> doneThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // requires 로 이미 깐 것

        void Say(string id, string state, string msg)
        {
            Core.Log("[" + id + "] " + state + " " + msg);
            if (Report != null) Report(id, state, msg);
        }

        public bool Install(CatalogData cat, string id)
        {
            try
            {
                if (id == "manager") InstallManager(cat);
                else
                {
                    var it = cat.Find(id);
                    if (it == null) throw new Exception("목록에 없는 항목입니다: " + id);
                    if (doneThisRun.Contains(id)) { Say(id, "done", it.Version + " 설치 완료"); return true; }
                    if (SkipIfCurrent)
                    {
                        string have = Catalog.InstalledVersion(it);
                        if (have != null && have != "설치됨" && Core.CompareVersions(have, it.Version) >= 0)
                        {
                            Say(id, "done", "이미 최신 (" + have + ")");
                            return true;
                        }
                    }
                    InstallWithRequires(cat, it);
                }
                return true;
            }
            catch (Exception e)
            {
                Core.Log("[" + id + "] 실패: " + e);
                Say(id, "error", e.Message);
                return false;
            }
        }

        string Fetch(string id, string url, long size, string sha, string fileName)
        {
            string dest = Path.Combine(Paths.Cache, fileName);
            Core.DownloadFile(url, size, sha, dest, delegate(long d, long t)
            {
                Say(id, "downloading", "받는 중 " + Core.Mb(d) + " / " + Core.Mb(t));
            });
            return dest;
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        /// <summary>requires 에 적힌 항목 중 아직 안 깔린 것을 먼저 깔고 (그것의 requires 도 차례로) 이 항목을 깐다.</summary>
        public void InstallWithRequires(CatalogData cat, Item it)
        {
            InstallWithRequires(cat, it, new HashSet<string>());
        }

        void InstallWithRequires(CatalogData cat, Item it, HashSet<string> visiting)
        {
            if (!visiting.Add(it.Id)) return;   // 서로를 requires 로 적은 실수 — 무한 반복 막기
            foreach (var rid in it.Requires)
            {
                var r = cat == null ? null : cat.Find(rid);
                if (r == null) { Core.Log("[" + it.Id + "] requires 의 '" + rid + "' 가 목록에 없음 — 건너뜀"); continue; }
                if (Catalog.InstalledVersion(r) != null) continue;
                Say(it.Id, "installing", "먼저 필요한 " + r.Name + " 설치 중");
                InstallWithRequires(cat, r, visiting);
            }
            InstallItem(it);
            doneThisRun.Add(it.Id);
        }

        /// <summary>이미 깔린 항목에 빠진 requires 만 채운다 (설치기의 '이미 설치됨' 단계).</summary>
        public void InstallMissingRequires(CatalogData cat, Item it)
        {
            foreach (var rid in it.Requires)
            {
                var r = cat == null ? null : cat.Find(rid);
                if (r == null || Catalog.InstalledVersion(r) != null) continue;
                Say(it.Id, "installing", "빠진 " + r.Name + " 설치 중");
                InstallWithRequires(cat, r);
            }
        }

        public void InstallItem(Item it)
        {
            if (string.IsNullOrEmpty(it.Sha256)) throw new Exception("목록에 SHA256 이 없어 설치하지 않습니다.");
            string ext = it.Kind == "driver" ? ".zip" : ".exe";
            string file = Fetch(it.Id, it.Url, it.Size, it.Sha256, SafeName(it.Id + "-" + it.Version) + ext);

            if (it.Kind == "driver")
            {
                Say(it.Id, "installing", "드라이버 설치 중");
                string dir = Path.Combine(Paths.Cache, SafeName(it.Id + "-" + it.Version));
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Unzip(file, dir);
                int code = Run(Sys32("pnputil.exe"), "/add-driver \"" + dir + "\\*.inf\" /subdirs /install", 10 * 60 * 1000);
                if (code == 3010) NeedReboot = true;
                else if (code != 0 && code != 259) throw new Exception("드라이버 설치 실패 (pnputil 코드 " + code + ")");
                foreach (var name in it.Printers)
                {
                    string n = name.Trim().Replace("'", "''");
                    Run(Sys32(@"WindowsPowerShell\v1.0\powershell.exe"),
                        "-NoProfile -ExecutionPolicy Bypass -Command \"Add-PrinterDriver -Name '" + n + "' -ErrorAction Stop\"", 5 * 60 * 1000);
                }
                try { Directory.Delete(dir, true); } catch { }
                RemoveOlderDriverPackages(it);
            }
            else
            {
                // 처음 까는 것이면, 따로 깔려 있던 같은 제조사 드라이버를 먼저 치운다 (예: Evolis 설치 프로그램은
                // 자기가 깔지 않은 Evolis 드라이버가 있으면 멈춘다). 이미 깔린 프로그램의 업데이트 때는 건드리지 않는다.
                if (it.Replaces != null && Catalog.InstalledVersion(it) == null)
                {
                    Say(it.Id, "installing", "따로 깔린 드라이버 정리 중");
                    RemoveDriverFamily(it.Replaces);
                }
                // 켜져 있으면 파일이 잠겨 설치가 실패하고 되돌리면서 파일을 지우기까지 한다 (SMART Utility 에서 확인) — 설치 프로그램의
                // /CLOSEAPPLICATIONS 는 SYSTEM 에서 사용자 화면의 프로그램을 못 닫는다. 먼저 닫고, 다시 켜는 것은 관리센터 쪽(Updater)이 한다.
                StopPrograms(it);
                UninstallOld(it);
                Say(it.Id, "installing", "설치 중");
                int code = it.Children.Count > 0 ? RunWithChildren(it, file) : Run(file, it.Args, 30 * 60 * 1000);
                if (code == 3010 || code == 1641 || it.RebootCodes.Contains(code)) NeedReboot = true;
                else if (code != 0 && code != 1638)
                {
                    // 제조사마다 '끝났지만 재부팅 필요' 를 제 나름의 코드로 알린다 — 실제로 그 판이 깔렸으면 성공으로 본다
                    string have = Catalog.InstalledVersion(it);
                    if (have != null && have != "설치됨" && Core.CompareVersions(have, it.Version) >= 0)
                    {
                        Core.Log("[" + it.Id + "] 종료 코드 " + code + " 이지만 " + have + " 가 설치됨 — 성공 (재부팅 권장)");
                        NeedReboot = true;
                    }
                    else
                    {
                        string why = ReasonFromLog(it.Args);
                        throw new Exception("설치 실패 (코드 " + code + ")" + (why != null ? " — " + why : ""));
                    }
                }
            }
            if (it.Kind != "driver") StopPrograms(it, true, null);   // SYSTEM 으로 돈 설치 프로그램이 띄운 것 (아무도 못 보는 화면) — 사용자 화면에서 다시 켠다
            EnsureExtras(it);
            Say(it.Id, "done", it.Version + " 설치 완료");
        }

        // ------------------------------------------------------------ 로그인 때 켤 프로그램 (catalog 의 "autostart")
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>설치 뒤에 맞출 것들 — SysWOW64 사본(wow64) · 자동 실행(autostart). 매번 불러도 된다.</summary>
        public static void EnsureExtras(Item it)
        {
            EnsureWow64(it);
            EnsureAutostart(it);
        }

        static string RealSystem32()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(win, Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32");
        }

        static string SysWow64()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        }

        /// <summary>wow64 에 적힌 파일을 System32 → SysWOW64 로 복사한다 (없거나 다르면). IDP 드라이버는 32비트 도구 · DLL 을
        /// System32(64비트 자리)에 넣는데, 32비트 프로그램이 System32 를 보면 Windows 가 SysWOW64 로 돌려 보내 watch.dll ·
        /// SmartComm2.dll 을 못 찾는다 ("watch.dll 이 없어서 진행할 수 없습니다").</summary>
        public static void EnsureWow64(Item it)
        {
            if (it == null || it.Wow64.Count == 0 || !Environment.Is64BitOperatingSystem) return;
            string src = RealSystem32(), dst = SysWow64();
            int copied = 0, same = 0;
            // Watchman 등이 켜져 있으면 SysWOW64 의 watch.dll 이 잠겨 복사가 안 된다 — 바꿀 것이 있을 때만 먼저 닫는다
            // (다시 켜는 것은 사용자 쪽 — Updater · 자동 확인이 StartAutostarts 로)
            if (Wow64Missing(it)) StopPrograms(it, false, null);
            foreach (var pat in it.Wow64)
            {
                string[] files;
                try { files = Directory.GetFiles(src, pat); } catch (Exception e) { Core.Log("[" + it.Id + "] " + pat + " 찾기 실패: " + e.Message); continue; }
                foreach (var f in files)
                {
                    string to = Path.Combine(dst, Path.GetFileName(f));
                    try
                    {
                        var a = new FileInfo(f);
                        var b = new FileInfo(to);
                        if (b.Exists && b.Length == a.Length && b.LastWriteTimeUtc == a.LastWriteTimeUtc) { same++; continue; }
                        CopyOver(f, to);
                        copied++;
                    }
                    catch (Exception e) { Core.Log("[" + it.Id + "] SysWOW64 복사 실패 " + Path.GetFileName(f) + ": " + e.Message); }
                }
            }
            if (copied > 0) Core.Log("[" + it.Id + "] 32비트 파일 SysWOW64 에 복사: " + copied + "개 (이미 같음 " + same + "개)");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string from, string to, int flags);
        const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        /// <summary>to 를 from 으로 덮어쓴다. 잠겨 있으면(어떤 프로그램이 그 DLL 을 쓰는 중) 몇 번 다시 해 보고, 그래도 안 되면
        /// 쓰는 중인 파일의 이름을 바꿔 치우고(실행 중인 DLL 도 이름은 바꿀 수 있다) 새 파일을 넣는다 — 치운 것은 재부팅 때 지운다.</summary>
        public static void CopyOver(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException)
                {
                    if (i < 5) { Thread.Sleep(1000); continue; }
                    if (!File.Exists(to)) throw;
                    string aside = to + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Move(to, aside);
                    File.Copy(from, to, true);
                    if (!MoveFileEx(aside, null, MOVEFILE_DELAY_UNTIL_REBOOT)) Core.Log("치운 파일 재부팅 때 지우기 예약 실패: " + aside);
                    Core.Log("쓰는 중이라 이름 바꿔 치우고 넣음: " + Path.GetFileName(to) + " (옛 것은 재부팅 때 지움)");
                    return;
                }
            }
        }

        public static bool Wow64Missing(Item it)
        {
            if (it.Wow64.Count == 0 || !Environment.Is64BitOperatingSystem) return false;
            string src = RealSystem32(), dst = SysWow64();
            foreach (var pat in it.Wow64)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(src, pat))
                    {
                        var b = new FileInfo(Path.Combine(dst, Path.GetFileName(f)));
                        var a = new FileInfo(f);
                        if (!b.Exists || b.Length != a.Length || b.LastWriteTimeUtc != a.LastWriteTimeUtc) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>HKLM Run 에 그 프로그램을 적는다 (관리자 · SYSTEM). 파일이 없으면 (드라이버가 아직 안 넣었으면) 건너뛴다.
        /// 이미 같은 값이면 그대로 — 매번 불러도 된다.</summary>
        public static void EnsureAutostart(Item it)
        {
            var a = it == null ? null : it.Autostart;
            if (a == null) return;
            try
            {
                if (!File.Exists(a.Path)) { Core.Log("[" + it.Id + "] 자동 실행 건너뜀 — 파일 없음: " + a.Path); return; }
                using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var run = hk.CreateSubKey(RunKey))
                {
                    if (!string.Equals(run.GetValue(a.Name) as string, a.Command, StringComparison.OrdinalIgnoreCase))
                    {
                        run.SetValue(a.Name, a.Command);
                        Core.Log("[" + it.Id + "] 자동 실행 등록: " + a.Name + " = " + a.Command);
                    }
                }
                RemoveStartupLinks(it.Id, a.StartupLink);
            }
            catch (Exception e) { Core.Log("[" + it.Id + "] 자동 실행 등록 실패: " + e.Message); }
        }

        /// <summary>모든 사용자(+ 공용 · SYSTEM)의 '시작 프로그램' 폴더에서 그 바로 가기를 지운다 — HKLM Run 과 겹쳐 두 번 켜지지 않게.</summary>
        static void RemoveStartupLinks(string id, string link)
        {
            if (string.IsNullOrEmpty(link)) return;
            if (!link.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) link += ".lnk";
            const string rel = @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
            var dirs = new List<string>();
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\config\systemprofile\" + rel));
            try
            {
                string users = Path.GetDirectoryName(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public");
                foreach (var u in Directory.GetDirectories(users)) dirs.Add(Path.Combine(u, rel));
            }
            catch (Exception e) { Core.Log("사용자 폴더 목록 실패: " + e.Message); }
            foreach (var d in dirs)
            {
                string f = Path.Combine(d, link);
                try { if (File.Exists(f)) { File.Delete(f); Core.Log("[" + id + "] 시작 프로그램 바로 가기 지움 (HKLM Run 으로 대신): " + f); } }
                catch (Exception e) { Core.Log("[" + id + "] 바로 가기 지우기 실패 " + f + ": " + e.Message); }
            }
        }

        /// <summary>깔려 있는데 아직 안 맞춘 것이 있나 — HKLM Run 의 자동 실행 · SysWOW64 사본 (사용자 권한으로 읽기만).</summary>
        public static bool AutostartMissing(CatalogData cat)
        {
            if (cat == null) return false;
            foreach (var it in cat.Items)
                if (it.Wow64.Count > 0 && Catalog.InstalledVersion(it) != null && Wow64Missing(it)) return true;
            try
            {
                using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var run = hk.OpenSubKey(RunKey))
                {
                    foreach (var it in cat.Items)
                    {
                        var a = it.Autostart;
                        if (a == null || !File.Exists(a.Path) || Catalog.InstalledVersion(it) == null) continue;
                        if (run == null || !string.Equals(run.GetValue(a.Name) as string, a.Command, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch (Exception e) { Core.Log("자동 실행 확인 실패: " + e.Message); }
            return false;
        }

        /// <summary>깔려 있는 항목 모두의 자동 실행을 맞춘다 — 그 기능이 생기기 전에 깔린 PC 도 다음 설치 때 고쳐진다.</summary>
        public static void EnsureAutostarts(CatalogData cat)
        {
            if (cat == null) return;
            foreach (var it in cat.Items)
                if ((it.Autostart != null || it.Wow64.Count > 0) && Catalog.InstalledVersion(it) != null) EnsureExtras(it);
        }

        /// <summary>지금 로그인한 사용자 화면에 자동 실행 프로그램을 켠다 (안 켜져 있을 때만). fromElevated 면 관리자 권한을 물려주지 않게
        /// explorer 로 띄운다. SYSTEM 서비스에서는 부르지 않는다 (사용자 화면이 없다).</summary>
        public static void StartAutostarts(CatalogData cat, bool fromElevated)
        {
            if (cat == null) return;
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var it in cat.Items)
            {
                var a = it.Autostart;
                if (a == null || !File.Exists(a.Path)) continue;
                try
                {
                    bool running = false;
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(a.Path)))
                        using (p) if (p.SessionId == session) running = true;
                    if (running) continue;
                    if (fromElevated) Process.Start("explorer.exe", "\"" + a.Path + "\"");
                    else
                    {
                        var psi = new ProcessStartInfo(a.Path, a.Args ?? "");
                        psi.WorkingDirectory = Path.GetDirectoryName(a.Path);
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                    }
                    Core.Log("[" + it.Id + "] 켬: " + a.Path);
                }
                catch (Exception e) { Core.Log("[" + it.Id + "] 켜기 실패: " + e.Message); }
            }
        }

        /// <summary>설치 프로그램을 돌리면서, 그것이 조용히 옵션 없이 띄우는 하위 설치 프로그램(it.Children)을 지켜본다. 뜨면 그 exe 를
        /// 복사해 두고 창을 닫는다(taskkill /T) — SYSTEM 으로 돌 때는 아무도 못 보는 창이라 그대로 두면 끝나지 않는다. 바깥 설치가
        /// 끝나면 복사해 둔 것을 조용히 옵션으로 다시 실행한다. 바깥 설치의 종료 코드를 돌려준다.</summary>
        int RunWithChildren(Item it, string file)
        {
            var copies = new List<KeyValuePair<ChildInstaller, string>>();
            var stop = new ManualResetEvent(false);
            int outerPid = 0;
            var watcher = new Thread(delegate()
            {
                var seen = new HashSet<int>();
                while (!stop.WaitOne(400))
                {
                    int outer = Thread.VolatileRead(ref outerPid);
                    if (outer == 0) continue;
                    try
                    {
                    foreach (var c in it.Children)
                    {
                        foreach (var p in Process.GetProcessesByName(c.Process))
                        {
                            using (p)
                            {
                                if (!seen.Add(p.Id)) continue;
                                try
                                {
                                    // 바깥 설치 프로그램이 띄운 것(자손)만 — 이름만 같은 다른 프로세스를 SYSTEM 으로 실행하는 일이 없게
                                    if (!IsDescendantOf(p.Id, outer)) { Core.Log("[" + it.Id + "] " + c.Process + " (PID " + p.Id + ") 는 이 설치가 띄운 것이 아님 — 무시"); continue; }
                                    string path = ProcessPath(p.Id);
                                    if (path == null || !File.Exists(path)) throw new Exception("실행 파일 위치를 모름");
                                    string copy = Path.Combine(Paths.Cache, SafeName(it.Id + "-" + Path.GetFileName(path)));
                                    File.Copy(path, copy, true);
                                    Core.Log("[" + it.Id + "] 하위 설치 " + c.Process + " (PID " + p.Id + ") 복사: " + copy + " — 창을 닫고 나중에 조용히 다시 실행");
                                    Run(Sys32("taskkill.exe"), "/PID " + p.Id + " /T /F", 30000);
                                    lock (copies) copies.Add(new KeyValuePair<ChildInstaller, string>(c, copy));
                                }
                                catch (Exception e) { Core.Log("[" + it.Id + "] 하위 설치 " + c.Process + " 가로채기 실패: " + e.Message); }
                            }
                        }
                    }
                    }
                    catch (Exception e) { Core.Log("[" + it.Id + "] 하위 설치 감시 오류 (계속): " + e.Message); }
                }
            });
            watcher.IsBackground = true;
            watcher.Start();
            int code;
            try { code = Run(file, it.Args, 30 * 60 * 1000, delegate(int pid) { Interlocked.Exchange(ref outerPid, pid); }); }
            finally { stop.Set(); watcher.Join(60000); }

            List<KeyValuePair<ChildInstaller, string>> list;
            lock (copies) list = new List<KeyValuePair<ChildInstaller, string>>(copies);
            foreach (var kv in list)
            {
                Say(it.Id, "installing", Path.GetFileNameWithoutExtension(kv.Value) + " 조용히 설치 중");
                int cc = Run(kv.Value, kv.Key.Args, 30 * 60 * 1000);
                if (cc == 3010 || cc == 1641 || it.RebootCodes.Contains(cc)) NeedReboot = true;
                else if (cc != 0)
                {
                    string why = ReasonFromLog(kv.Key.Args);
                    throw new Exception(kv.Key.Process + " 설치 실패 (코드 " + cc + ")" + (why != null ? " — " + why : ""));
                }
            }
            return code;
        }

        void StopPrograms(Item it)
        {
            StopPrograms(it, false, delegate(string m) { Say(it.Id, "installing", m); });
        }

        /// <summary>그 항목의 프로그램(WatchedPrograms)이 켜져 있으면 닫는다 — 실행 파일 경로가 정확히 같은 것만 (경로를 모르면 건너뜀).
        /// session0Only 면 SYSTEM 화면(세션 0)에 남은 것만.</summary>
        static void StopPrograms(Item it, bool session0Only, Action<string> say)
        {
            foreach (var t in it.WatchedPrograms())
            {
                if (string.IsNullOrEmpty(t.Path)) continue;
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(t.Path)))
                {
                    using (p)
                    {
                        try
                        {
                            if (session0Only && p.SessionId != 0) continue;
                            string path = ProcessPath(p.Id);
                            if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(t.Path), StringComparison.OrdinalIgnoreCase)) continue;
                            if (say != null) say(Path.GetFileName(t.Path) + " 닫는 중");
                            Core.Log("[" + it.Id + "] 닫음 " + path + " (PID " + p.Id + ", 세션 " + p.SessionId + ")");
                            Run(Sys32("taskkill.exe"), "/PID " + p.Id + " /F", 30000);
                            try { p.WaitForExit(10000); } catch { }   // 파일 잠금이 풀릴 때까지
                        }
                        catch (Exception e) { Core.Log("[" + it.Id + "] " + t.Path + " 닫기 실패: " + e.Message); }
                    }
                }
            }
        }

        /// <summary>pid 가 ancestor 의 자손인가 (부모를 몇 단계 거슬러 올라가 본다).</summary>
        static bool IsDescendantOf(int pid, int ancestor)
        {
            int cur = pid;
            for (int i = 0; i < 8 && cur > 4; i++)
            {
                int parent = 0;
                using (var s = new System.Management.ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + cur))
                using (var r = s.Get())
                    foreach (System.Management.ManagementObject o in r) using (o) parent = Convert.ToInt32(o["ParentProcessId"]);
                if (parent == ancestor) return true;
                if (parent == 0 || parent == cur) return false;
                cur = parent;
            }
            return false;
        }

        /// <summary>uninstallFirst: 깔려 있으면 옛 판 제거 프로그램을 조용히 돌리고 끝날 때까지 기다린다. Inno 제거 프로그램은 자신을
        /// 임시 폴더로 복사해 다시 띄우고 곧바로 끝나므로, 제거 프로그램 파일이 사라질 때까지(최대 5분) 기다린다.</summary>
        void UninstallOld(Item it)
        {
            var u = it.UninstallFirst;
            if (u == null || string.IsNullOrEmpty(u.Path) || !File.Exists(u.Path)) return;
            Say(it.Id, "installing", "이전 판 조용히 제거 중");
            Run(u.Path, u.Args, 10 * 60 * 1000);
            var until = DateTime.Now.AddMinutes(5);
            while (File.Exists(u.Path) && DateTime.Now < until) Thread.Sleep(1000);
            if (File.Exists(u.Path)) Core.Log("[" + it.Id + "] 이전 판 제거 프로그램이 5분 안에 끝나지 않음 — 그대로 설치 진행");
            else Core.Log("[" + it.Id + "] 이전 판 제거 끝");
        }

        static string ProcessPath(int pid)
        {
            using (var s = new System.Management.ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = " + pid))
            using (var r = s.Get())
                foreach (System.Management.ManagementObject o in r) using (o) return o["ExecutablePath"] as string;
            return null;
        }

        /// <summary>새 판을 넣은 뒤 드라이버 저장소에 남은 같은 INF 의 예전 판을 지운다 — 업데이트를 거듭해도 깔끔하게.
        /// /force 없이 지우므로 아직 어떤 장치가 쓰고 있는 판은 Windows 가 거절해 그대로 남는다 (안전).</summary>
        void RemoveOlderDriverPackages(Item it)
        {
            if (string.IsNullOrEmpty(it.Inf) || string.IsNullOrEmpty(it.Version)) return;
            try
            {
                string inf = it.Inf.Replace("'", "''"), ver = it.Version.Replace("'", "''");
                string ps =
                    "$inf='" + inf + "'; $new=[version]'" + ver + "'; " +
                    "Get-WindowsDriver -Online | Where-Object { (Split-Path $_.OriginalFileName -Leaf) -eq $inf -and [version]$_.Version -lt $new } | " +
                    "ForEach-Object { Write-Output ('remove ' + $_.Driver + ' ' + $_.Version); pnputil /delete-driver $_.Driver | Select-Object -Last 1 }";
                Run(Sys32(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"", 10 * 60 * 1000);
            }
            catch (Exception e) { Core.Log("예전 드라이버 판 정리 실패 (무시): " + e.Message); }
        }

        /// <summary>그 제조사의 인쇄 드라이버 · 프린터 대기열 · 드라이버 저장소 꾸러미를 지운다.
        /// 프린터는 USB 를 다시 꽂거나(이미 꽂혀 있으면 새 드라이버가 깔릴 때) Windows 가 다시 만든다.</summary>
        void RemoveDriverFamily(DriverFamily f)
        {
            try
            {
                string man = (f.Manufacturer ?? "").Replace("'", "''"), inf = (f.Inf ?? "").Replace("'", "''");
                string ps =
                    "$m='" + man + "'; $inf='" + inf + "'; " +
                    "$d = @(Get-PrinterDriver | Where-Object { $m -and $_.Manufacturer -like ($m + '*') }); " +
                    "$names = @($d | ForEach-Object { $_.Name }); " +
                    "Get-Printer | Where-Object { $names -contains $_.DriverName } | ForEach-Object { Write-Output ('printer ' + $_.Name); Remove-Printer -Name $_.Name -ErrorAction SilentlyContinue }; " +
                    "foreach ($x in $d) { Remove-PrinterDriver -Name $x.Name -PrinterEnvironment $x.PrinterEnvironment -ErrorAction SilentlyContinue }; " +
                    "if (@(Get-PrinterDriver | Where-Object { $names -contains $_.Name }).Count -gt 0) { Restart-Service Spooler -Force; Start-Sleep 3; " +
                    "  foreach ($x in $d) { Remove-PrinterDriver -Name $x.Name -PrinterEnvironment $x.PrinterEnvironment -ErrorAction SilentlyContinue } }; " +
                    "if ($inf) { Get-WindowsDriver -Online | Where-Object { (Split-Path $_.OriginalFileName -Leaf) -eq $inf } | " +
                    "  ForEach-Object { Write-Output ('package ' + $_.Driver + ' ' + $_.Version); pnputil /delete-driver $_.Driver /uninstall /force | Select-Object -Last 1 } }";
                Run(Sys32(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"", 10 * 60 * 1000);
            }
            catch (Exception e) { Core.Log("드라이버 정리 실패 (계속 진행): " + e.Message); }
        }

        /// <summary>설치 인자의 /LOG="…" 로그에서 실패 이유로 보이는 줄을 찾아 돌려준다 (Inno Setup 등).</summary>
        static string ReasonFromLog(string args)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(args ?? "", "/LOG=\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success || !File.Exists(m.Groups[1].Value)) return null;
                Core.Log("설치 로그: " + m.Groups[1].Value);
                var lines = File.ReadAllLines(m.Groups[1].Value);
                string found = null, star = null;
                foreach (var raw in lines)
                {
                    string l = raw.Length > 24 ? raw.Substring(24).Trim() : raw.Trim();   // 앞의 날짜 · 시각 떼기
                    Core.Log("    | " + l);
                    if (System.Text.RegularExpressions.Regex.IsMatch(l, "abort|cannot|error|fail|restart is needed|PrepareToInstall", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        && !l.StartsWith("Got EAbort"))
                    {
                        found = l;
                        if (star == null && l.StartsWith("***")) star = l;   // 제조사가 직접 적은 설명 줄 (예: Evolis)
                    }
                }
                if (star != null) found = star;
                if (found != null && found.Length > 180) found = found.Substring(0, 180) + "…";
                return found;
            }
            catch { return null; }
        }

        public void InstallManager(CatalogData cat)
        {
            if (string.IsNullOrEmpty(cat.ManagerUrl) || string.IsNullOrEmpty(cat.ManagerSha256))
                throw new Exception("목록에 관리센터 파일 정보가 없습니다.");
            string file = Fetch("manager", cat.ManagerUrl, cat.ManagerSize, cat.ManagerSha256, "CoreDotManager-" + SafeName(cat.ManagerVersion) + ".exe");
            Say("manager", "installing", "관리센터 바꾸는 중");
            ReplaceManager(file);
            try { SetupMode.WriteUninstallEntry(cat.ManagerVersion); } catch (Exception e) { Core.Log("제어판 판 번호 갱신 실패: " + e.Message); }
            Say("manager", "done", cat.ManagerVersion + " 설치 완료");
        }

        /// <summary>돌고 있는 exe 도 이름은 바꿀 수 있다 — 지금 것을 .old 로 밀어 두고 새 것을 넣는다 (.old 는 다음 실행 때 지움).</summary>
        public static void ReplaceManager(string newFile)
        {
            Directory.CreateDirectory(Paths.ProgramDir);
            string exe = Paths.ManagerExe;
            if (string.Equals(Path.GetFullPath(newFile), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(exe))
            {
                string old = exe + ".old";
                try { if (File.Exists(old)) File.Delete(old); }
                catch { old = exe + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".old"; }
                File.Move(exe, old);
            }
            File.Copy(newFile, exe, true);
        }

        public static void Unzip(string zipFile, string dir)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            using (var zip = ZipFile.OpenRead(zipFile))
            {
                foreach (var en in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, en.FullName.Replace('/', '\\')));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // zip slip
                    if (en.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    en.ExtractToFile(target, true);
                }
            }
        }

        public static string Sys32(string exe)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sub = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            return Path.Combine(win, sub, exe);
        }

        public static int Run(string file, string args, int timeoutMs)
        {
            return Run(file, args, timeoutMs, null);
        }

        /// <param name="started">프로세스가 뜨면 PID 를 알려 준다 (하위 설치 감시용)</param>
        public static int Run(string file, string args, int timeoutMs, Action<int> started)
        {
            Core.Log("> " + Path.GetFileName(file) + " " + args);
            var psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = oem;
            psi.StandardErrorEncoding = oem;
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { if (!string.IsNullOrWhiteSpace(e.Data)) Core.Log("    " + e.Data); };
                p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (!string.IsNullOrWhiteSpace(e.Data)) Core.Log("    ! " + e.Data); };
                p.Start();
                if (started != null) { try { started(p.Id); } catch { } }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("시간 초과: " + Path.GetFileName(file));
                }
                // 출력 끝까지 기다리기 — 손자 프로세스(예: 상주 프로그램)가 출력 통로를 물려받으면 영영 안 끝나므로 15초까지만
                var drain = new Thread(delegate() { try { p.WaitForExit(); } catch { } });
                drain.IsBackground = true;
                drain.Start();
                drain.Join(15000);
                Core.Log("  종료 코드 " + p.ExitCode);
                return p.ExitCode;
            }
        }

        public static void ClearSpooler()
        {
            Run(Sys32("net.exe"), "stop spooler /y", 60000);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\spool\PRINTERS");
            foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
            Run(Sys32("net.exe"), "start spooler", 60000);
        }
    }

    // ================================================================== SYSTEM 서비스 (--service)
    public static class Service
    {
        static readonly object statusLock = new object();

        public static int Run()
        {
            SetupMode.EnsureDataDir();   // 1.0.1 이하가 망가뜨린 파일 권한도 여기서 고쳐진다
            Core.LogFile = Paths.ServiceLog;
            bool created;
            using (var m = new Mutex(true, @"Global\CoreDotInstaller", out created))
            {
                if (!created) { m.WaitOne(TimeSpan.FromMinutes(60)); }
                try
                {
                    // 늦게 온 요청까지 다 처리
                    for (int round = 0; round < 20 && !handedOff; round++)
                    {
                        var files = Directory.GetFiles(Paths.Requests, "*.json");
                        if (files.Length == 0) break;
                        Array.Sort(files, delegate(string a, string b) { return File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)); });
                        foreach (var f in files) { Handle(f); if (handedOff) break; }
                    }
                }
                finally { try { m.ReleaseMutex(); } catch { } }
            }
            return 0;
        }

        // 관리센터를 새 판으로 바꾼 뒤 남은 항목은 새 판이 맡도록 넘긴다 (예전 판이 계속 설치하면 새 판의 고친 점이 안 먹는다)
        static bool handedOff;

        static void Handle(string file)
        {
            Dictionary<string, object> req;
            try { req = Core.ParseJson(File.ReadAllText(file, Encoding.UTF8)); }
            catch (Exception e) { Core.Log("요청 읽기 실패 " + file + ": " + e.Message); Core.TryDelete(file); return; }
            Core.TryDelete(file);

            string rid = Path.GetFileNameWithoutExtension(file);
            string action = Core.Str(req, "action") ?? "install";
            var ids = new List<string>();
            var arr = req.ContainsKey("ids") ? req["ids"] as System.Collections.IEnumerable : null;
            if (arr != null) foreach (object o in arr) if (o != null) ids.Add(o.ToString());
            Core.Log("==== 요청 " + rid + " " + action + " [" + string.Join(", ", ids.ToArray()) + "]");

            var status = new Dictionary<string, object>();
            var items = new Dictionary<string, object>();
            status["request"] = rid;
            status["done"] = false;
            status["reboot"] = false;
            status["items"] = items;
            foreach (var id in ids) items[id] = State("waiting", "기다리는 중");
            // 앞 판이 이미 끝낸 항목 (넘겨받은 요청)
            var prior = req.ContainsKey("done") ? req["done"] as Dictionary<string, object> : null;
            if (prior != null) foreach (var kv in prior) items[kv.Key] = State("done", kv.Value == null ? "완료" : kv.Value.ToString());
            if (req.ContainsKey("reboot") && req["reboot"] is bool && (bool)req["reboot"]) status["reboot"] = true;
            WriteStatus(status);

            try
            {
                if (action == "clear-spool")
                {
                    Engine.ClearSpooler();
                    status["message"] = "인쇄 대기열을 비웠습니다";
                }
                else
                {
                    var cat = Catalog.Fetch();
                    var engine = new Engine();
                    engine.SkipIfCurrent = req.ContainsKey("auto") && req["auto"] is bool && (bool)req["auto"];
                    engine.Report = delegate(string id, string state, string msg)
                    {
                        lock (statusLock) { items[id] = State(state, msg); }
                        WriteStatus(status);
                    };
                    for (int i = 0; i < ids.Count; i++)
                    {
                        string id = ids[i];
                        if (id == "coredotprinter") continue;   // 사용자 쪽에서 (앱을 사용자 화면에서 다시 켜야 해서)
                        bool ok = engine.Install(cat, id);
                        if (id == "manager" && ok && i < ids.Count - 1)
                        {
                            HandOff(rid, ids.GetRange(i + 1, ids.Count - i - 1), items, engine.NeedReboot || (status["reboot"] is bool && (bool)status["reboot"]), engine.SkipIfCurrent);
                            WriteStatus(status);
                            return;   // done 은 새 판이 쓴다
                        }
                    }
                    Engine.EnsureAutostarts(cat);
                    if (engine.NeedReboot) status["reboot"] = true;
                }
            }
            catch (Exception e)
            {
                Core.Log("요청 실패: " + e);
                status["error"] = e.Message;
            }
            status["done"] = true;
            WriteStatus(status);
            Core.Log("==== 요청 " + rid + " 끝");
        }

        /// <summary>같은 요청 번호로 남은 항목을 다시 적고 작업을 한 번 더 부른다 — 작업 설정이 '줄 세우기' 라 지금 것이
        /// 끝나면 새 판 exe 로 이어서 돈다. 관리센터 창은 같은 요청 번호의 상태를 계속 본다.</summary>
        static void HandOff(string rid, List<string> rest, Dictionary<string, object> items, bool reboot, bool auto)
        {
            var done = new Dictionary<string, object>();
            lock (statusLock)
            {
                foreach (var kv in items)
                {
                    var d = kv.Value as Dictionary<string, object>;
                    if (Core.Str(d, "state") == "done") done[kv.Key] = Core.Str(d, "message");
                }
                foreach (var id in rest) items[id] = State("waiting", "새 관리센터로 이어서 설치");
            }
            var req = new Dictionary<string, object>();
            req["action"] = "install";
            req["ids"] = rest;
            req["done"] = done;
            req["reboot"] = reboot;
            req["by"] = "handoff";
            req["auto"] = auto;
            File.WriteAllText(Path.Combine(Paths.Requests, rid + ".json"), Core.ToJson(req), Encoding.UTF8);
            handedOff = true;
            Core.Log("==== 요청 " + rid + " 남은 항목 [" + string.Join(", ", rest.ToArray()) + "] 을 새 관리센터로 넘김");
            try { Engine.Run(Engine.Sys32("schtasks.exe"), "/Run /TN \"" + ServiceClient.TaskPath + "\"", 30000); }
            catch (Exception e) { Core.Log("작업 다시 부르기 실패: " + e.Message); }
        }

        static Dictionary<string, object> State(string state, string msg)
        {
            var d = new Dictionary<string, object>();
            d["state"] = state;
            d["message"] = msg;
            return d;
        }

        static void WriteStatus(Dictionary<string, object> status)
        {
            lock (statusLock)
            {
                try
                {
                    string tmp = Paths.Status + ".tmp";
                    File.WriteAllText(tmp, Core.ToJson(status), Encoding.UTF8);
                    if (File.Exists(Paths.Status)) File.Replace(tmp, Paths.Status, null);
                    else File.Move(tmp, Paths.Status);
                }
                catch (Exception e) { Core.Log("상태 쓰기 실패: " + e.Message); }
            }
        }
    }

    // ================================================================== 사용자 쪽에서 서비스 부르기
    public static class ServiceClient
    {
        public const string TaskPath = @"\CoreDot\Installer";

        /// <summary>요청을 적고 서비스를 깨운다. 요청 id 를 돌려준다.</summary>
        /// <param name="interactive">false 면 작업을 못 부를 때 관리자 확인 창을 띄우지 않고 실패로 둔다 (자동 업데이트 — 아무도 없는 키오스크)</param>
        public static string Request(string action, IList<string> ids, bool interactive = true)
        {
            string rid = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var req = new Dictionary<string, object>();
            req["action"] = action;
            req["ids"] = ids;
            req["by"] = Environment.UserName;
            req["auto"] = !interactive;   // 자동 업데이트 — 서비스가 이미 최신인 것은 다시 깔지 않는다
            File.WriteAllText(Path.Combine(Paths.Requests, rid + ".json"), Core.ToJson(req), Encoding.UTF8);
            int code = Engine.Run(Engine.Sys32("schtasks.exe"), "/Run /TN \"" + TaskPath + "\"", 30000);
            if (code != 0 && !interactive)
            {
                Core.TryDelete(Path.Combine(Paths.Requests, rid + ".json"));
                throw new Exception("설치 작업을 부르지 못했습니다 (코드 " + code + ")");
            }
            if (code != 0)
            {
                // 작업이 없거나 못 부르면 — 관리자 확인을 받아 직접 실행
                Core.Log("설치 작업을 부르지 못함 (코드 " + code + ") — 관리자 권한으로 직접 실행");
                var psi = new ProcessStartInfo(Paths.ManagerExe, "--service");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            return rid;
        }

        public static Dictionary<string, object> ReadStatus()
        {
            try
            {
                using (var fs = new FileStream(Paths.Status, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    return Core.ParseJson(sr.ReadToEnd());
            }
            catch { return null; }
        }
    }
}

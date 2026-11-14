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
                    InstallItem(it);
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
                Say(it.Id, "installing", "설치 중");
                int code = Run(file, it.Args, 30 * 60 * 1000);
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
                    else throw new Exception("설치 실패 (코드 " + code + ")");
                }
            }
            Say(it.Id, "done", it.Version + " 설치 완료");
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
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("시간 초과: " + Path.GetFileName(file));
                }
                p.WaitForExit();
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
                    for (int round = 0; round < 20; round++)
                    {
                        var files = Directory.GetFiles(Paths.Requests, "*.json");
                        if (files.Length == 0) break;
                        Array.Sort(files, delegate(string a, string b) { return File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)); });
                        foreach (var f in files) Handle(f);
                    }
                }
                finally { try { m.ReleaseMutex(); } catch { } }
            }
            return 0;
        }

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
                    engine.Report = delegate(string id, string state, string msg)
                    {
                        lock (statusLock) { items[id] = State(state, msg); }
                        WriteStatus(status);
                    };
                    foreach (var id in ids)
                    {
                        if (id == "coredotprinter") continue;   // 사용자 쪽에서 (앱을 사용자 화면에서 다시 켜야 해서)
                        engine.Install(cat, id);
                    }
                    status["reboot"] = engine.NeedReboot;
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
        public static string Request(string action, IList<string> ids)
        {
            string rid = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var req = new Dictionary<string, object>();
            req["action"] = action;
            req["ids"] = ids;
            req["by"] = Environment.UserName;
            File.WriteAllText(Path.Combine(Paths.Requests, rid + ".json"), Core.ToJson(req), Encoding.UTF8);
            int code = Engine.Run(Engine.Sys32("schtasks.exe"), "/Run /TN \"" + TaskPath + "\"", 30000);
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

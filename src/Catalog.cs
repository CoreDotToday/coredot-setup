// 프로그램 목록(catalog.json) · 이 PC 에 깔린 판 찾기 · 무엇을 새로 깔거나 올려야 하는지
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoreDot
{
    public class Tool
    {
        public string Label, Path, Args;
    }

    public class DriverFamily
    {
        public string Inf, Manufacturer;
    }

    /// <summary>로그인할 때 켜 둘 프로그램 (HKLM Run). 예: IDP 드라이버가 System32 에 넣는 상태 감시 Watchman.exe</summary>
    public class Autostart
    {
        public string Name, Path, Args;
        public string StartupLink;   // (선택) 제조사 설치 프로그램이 사용자 '시작 프로그램' 폴더에 만드는 바로 가기 이름 — HKLM Run 과 겹쳐 두 번 켜지지 않게 지운다
        public string Command { get { return "\"" + Path + "\"" + (string.IsNullOrEmpty(Args) ? "" : " " + Args); } }
    }

    /// <summary>설치 프로그램이 안에서 띄우는 다른 설치 프로그램 — 조용히 옵션 없이 띄워 창이 뜨면, 그 파일을 복사해 두고 창을 닫은 뒤
    /// 조용히 옵션으로 다시 실행한다. 예: SMART Utility 설치가 띄우는 SMART_Agent-Setup.exe (/VERYSILENT 없이 실행됨)</summary>
    public class ChildInstaller
    {
        public string Process, Args;
    }

    public class Item
    {
        public List<string> Requires = new List<string>();          // (선택) 이것보다 먼저 깔려 있어야 하는 항목 id (예: .NET 런타임)
        public List<ChildInstaller> Children = new List<ChildInstaller>();
        public DriverFamily Replaces;              // exe: 처음 깔 때 먼저 지울 같은 제조사 드라이버 (예: Evolis)
        public Autostart Autostart;                // (선택) 설치 뒤 로그인 때마다 켤 프로그램
        public string Id, Name, Kind, Version, Url, Sha256;
        public long Size;
        public string Inf;                         // driver: 드라이버 저장소의 원래 INF 이름 (예: e1wf1b0e.inf)
        public string[] Printers = new string[0];  // driver: 인쇄 드라이버 이름들 (Add-PrinterDriver)
        public string Args;                        // exe: 조용히 설치 인자
        public string Detect;                      // exe: 제어판 프로그램 목록의 이름
        public string Open;                        // exe: [열기] 로 띄울 파일
        public List<int> RebootCodes = new List<int>();   // exe: '설치됨 · 재부팅 필요' 로 볼 종료 코드 (예: Evolis 500)
        public List<Tool> Tools = new List<Tool>();
        public List<Tool> Restart = new List<Tool>();   // (선택) 업데이트 때 꺼졌다면 다시 켤 프로그램 — 없으면 autostart · open 을 본다
        public List<string> Wow64 = new List<string>();
        public Tool UninstallFirst;   // (선택) exe: 이미 깔려 있으면 설치 전에 이것으로 옛 판을 조용히 지운다 — 설치 프로그램이 옛 판 제거를
                                      //  조용히 옵션 없이 띄워 SYSTEM 화면에서 '제거할까요?' 로 멈추는 경우 (예: SMART Utility)  // (선택) driver: System32 에 들어간 32비트 파일 중 SysWOW64 에도 둘 것 (* 가능)

        /// <summary>설치 · 업데이트 전에 닫고 끝나면 다시 켤 프로그램 — restart 가 있으면 그것, 없으면 autostart · open.</summary>
        public List<Tool> WatchedPrograms()
        {
            var list = new List<Tool>();
            if (Restart.Count > 0) list.AddRange(Restart);
            else
            {
                if (Autostart != null) list.Add(new Tool { Path = Autostart.Path, Args = Autostart.Args });
                if (!string.IsNullOrEmpty(Open)) list.Add(new Tool { Path = Open, Args = "" });
            }
            return list;
        }
    }

    public class CatalogData
    {
        public string ManagerVersion, ManagerUrl, ManagerSha256;
        public long ManagerSize;
        public List<Item> Items = new List<Item>();
        public bool FromCache;

        public Item Find(string id)
        {
            foreach (var it in Items) if (it.Id == id) return it;
            return null;
        }
    }

    public static class Paths
    {
        public static readonly string ProgramDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CoreDot");
        public static readonly string ManagerExe = Path.Combine(ProgramDir, "CoreDotManager.exe");
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CoreDot");
        public static readonly string Cache = Path.Combine(DataDir, "cache");          // SYSTEM · 관리자만 쓰기
        public static readonly string Requests = Path.Combine(DataDir, "requests");    // 사용자가 '이것들 설치해 줘' 를 적는 곳
        public static readonly string Status = Path.Combine(DataDir, "status.json");
        public static readonly string CatalogCache = Path.Combine(DataDir, "catalog.json");
        public static readonly string ServiceLog = Path.Combine(DataDir, @"logs\service.log");
        public static readonly string UserLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CoreDot\logs\manager.log");
    }

    public static class Catalog
    {
        public const string Repo = "CoreDotToday/coredot-setup";
        public const string Url = "https://raw.githubusercontent.com/" + Repo + "/main/catalog.json";

        /// <summary>최신 목록을 받는다. 안 되면 마지막으로 받아 둔 것.</summary>
        public static CatalogData Fetch()
        {
            try
            {
                string json = Core.GetText(Url + "?t=" + DateTime.UtcNow.Ticks, 20000);
                var c = Parse(json);
                try { File.WriteAllText(Paths.CatalogCache, json); } catch { }   // 서비스 · 설치기만 쓸 수 있다
                return c;
            }
            catch (Exception e)
            {
                Core.Log("목록 받기 실패: " + e.Message);
                if (File.Exists(Paths.CatalogCache))
                {
                    var c = Parse(File.ReadAllText(Paths.CatalogCache));
                    c.FromCache = true;
                    return c;
                }
                throw new Exception("프로그램 목록을 받지 못했습니다. 인터넷 연결을 확인하세요. (" + e.Message + ")");
            }
        }

        /// <summary>서비스 · 설치기가 마지막으로 받아 둔 목록 (사용자 쪽에서 읽기만). 없으면 null.</summary>
        public static CatalogData LoadCached()
        {
            try { return File.Exists(Paths.CatalogCache) ? Parse(File.ReadAllText(Paths.CatalogCache)) : null; }
            catch (Exception e) { Core.Log("받아 둔 목록 읽기 실패: " + e.Message); return null; }
        }

        public static CatalogData Parse(string json)
        {
            var o = Core.ParseJson(json);
            var c = new CatalogData();
            var m = o.ContainsKey("manager") ? o["manager"] as Dictionary<string, object> : null;
            if (m != null)
            {
                c.ManagerVersion = Core.Str(m, "version");
                c.ManagerUrl = Core.Str(m, "url");
                c.ManagerSha256 = Core.Str(m, "sha256");
                c.ManagerSize = Core.Num(m, "size");
            }
            var items = o.ContainsKey("items") ? o["items"] as IEnumerable : null;
            if (items != null)
            {
                foreach (object x in items)
                {
                    var d = x as Dictionary<string, object>;
                    if (d == null) continue;
                    var it = new Item();
                    it.Id = Core.Str(d, "id");
                    it.Name = Core.Str(d, "name") ?? it.Id;
                    it.Kind = Core.Str(d, "kind");
                    it.Version = Core.Str(d, "version");
                    it.Url = Core.Str(d, "url");
                    it.Sha256 = Core.Str(d, "sha256");
                    it.Size = Core.Num(d, "size");
                    it.Inf = Core.Str(d, "inf");
                    it.Args = Core.Str(d, "args") ?? "";
                    it.Detect = Core.Str(d, "detect");
                    it.Open = Core.Str(d, "open");
                    var pr = d.ContainsKey("printers") ? d["printers"] as IEnumerable : null;
                    if (pr != null)
                    {
                        var list = new List<string>();
                        foreach (object p in pr) if (p != null) list.Add(p.ToString());
                        it.Printers = list.ToArray();
                    }
                    var rp = d.ContainsKey("replaces") ? d["replaces"] as Dictionary<string, object> : null;
                    if (rp != null) it.Replaces = new DriverFamily { Inf = Core.Str(rp, "inf"), Manufacturer = Core.Str(rp, "manufacturer") };
                    var au = d.ContainsKey("autostart") ? d["autostart"] as Dictionary<string, object> : null;
                    if (au != null && !string.IsNullOrEmpty(Core.Str(au, "name")) && !string.IsNullOrEmpty(Core.Str(au, "path")))
                        it.Autostart = new Autostart { Name = Core.Str(au, "name"), Path = Expand(Core.Str(au, "path")), Args = Core.Str(au, "args") ?? "", StartupLink = Core.Str(au, "startupLink") };
                    var rq = d.ContainsKey("requires") ? d["requires"] as IEnumerable : null;
                    if (rq != null) foreach (object r in rq) if (r != null) it.Requires.Add(r.ToString());
                    var ch = d.ContainsKey("children") ? d["children"] as IEnumerable : null;
                    if (ch != null)
                        foreach (object x3 in ch)
                        {
                            var cd = x3 as Dictionary<string, object>;
                            if (cd == null || string.IsNullOrEmpty(Core.Str(cd, "process"))) continue;
                            it.Children.Add(new ChildInstaller { Process = Core.Str(cd, "process"), Args = Expand(Core.Str(cd, "args") ?? "") });
                        }
                    var rc = d.ContainsKey("rebootCodes") ? d["rebootCodes"] as IEnumerable : null;
                    if (rc != null) foreach (object x2 in rc) { try { it.RebootCodes.Add(Convert.ToInt32(x2)); } catch { } }
                    var tools = d.ContainsKey("tools") ? d["tools"] as IEnumerable : null;
                    if (tools != null)
                    {
                        foreach (object t in tools)
                        {
                            var td = t as Dictionary<string, object>;
                            if (td == null) continue;
                            it.Tools.Add(new Tool { Label = Core.Str(td, "label"), Path = Expand(Core.Str(td, "path")), Args = Core.Str(td, "args") ?? "" });
                        }
                    }
                    var uf = d.ContainsKey("uninstallFirst") ? d["uninstallFirst"] as Dictionary<string, object> : null;
                    if (uf != null && !string.IsNullOrEmpty(Core.Str(uf, "path")))
                        it.UninstallFirst = new Tool { Path = Expand(Core.Str(uf, "path")), Args = Core.Str(uf, "args") ?? "" };
                    var w6 = d.ContainsKey("wow64") ? d["wow64"] as IEnumerable : null;
                    if (w6 != null) foreach (object w in w6) if (w != null && w.ToString().Length > 0) it.Wow64.Add(w.ToString());
                    var rs = d.ContainsKey("restart") ? d["restart"] as IEnumerable : null;
                    if (rs != null)
                        foreach (object t in rs)
                        {
                            var td = t as Dictionary<string, object>;
                            if (td == null || string.IsNullOrEmpty(Core.Str(td, "path"))) continue;
                            it.Restart.Add(new Tool { Path = Expand(Core.Str(td, "path")), Args = Core.Str(td, "args") ?? "" });
                        }
                    it.Open = Expand(it.Open);
                    it.Args = Expand(it.Args);   // 예: /LOG="%ProgramData%\CoreDot\logs\..."
                    // "app" = exe 와 같다. 1.0.5 이하는 driver · exe 만 읽으므로 'app' 항목은 옛 관리센터에 보이지 않는다
                    // (requires · children 같은 새 기능이 필요한 항목을 옛 판이 잘못 까는 일이 없게)
                    if (it.Kind == "app") it.Kind = "exe";
                    if (string.IsNullOrEmpty(it.Id) || (it.Kind != "driver" && it.Kind != "exe")) continue;
                    c.Items.Add(it);
                }
            }
            return c;
        }

        static string Expand(string p)
        {
            return string.IsNullOrEmpty(p) ? p : Environment.ExpandEnvironmentVariables(p);
        }

        // ------------------------------------------------------------ 이 PC 에 깔린 판
        /// <summary>깔린 판. 없으면 null.</summary>
        public static string InstalledVersion(Item it)
        {
            try
            {
                if (it.Kind == "driver") return DriverStoreVersion(it.Inf);
                if (it.Kind == "exe") return UninstallVersion(it.Detect ?? it.Name);
            }
            catch (Exception e) { Core.Log("판 확인 실패 " + it.Id + ": " + e.Message); }
            return null;
        }

        /// <summary>드라이버 저장소(FileRepository)에 들어 있는 그 INF 의 DriverVer 중 가장 높은 것.</summary>
        public static string DriverStoreVersion(string inf)
        {
            if (string.IsNullOrEmpty(inf)) return null;
            string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\DriverStore\FileRepository");
            string best = null;
            foreach (var dir in Directory.GetDirectories(repo, inf + "_*"))
            {
                string file = Path.Combine(dir, inf);
                if (!File.Exists(file)) continue;
                var m = Regex.Match(File.ReadAllText(file), @"DriverVer\s*=\s*[^,\r\n]*,\s*([0-9][0-9.]*)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string v = m.Groups[1].Value.Trim();
                if (best == null || Core.CompareVersions(v, best) > 0) best = v;
            }
            return best;
        }

        /// <summary>제어판 프로그램 목록에서 찾는다. name 이 '*' 로 끝나면 그 앞부분으로 시작하는 이름
        /// (예: "Evolis Premium Suite 2*" — Evolis 는 이름에 판 번호를 붙인다).</summary>
        public static string UninstallVersion(string name)
        {
            string best = null;
            bool found = false;
            // * 는 아무 글자나 (예: "Microsoft Windows Desktop Runtime - 9.* (x64)")
            var rx = new Regex("^" + Regex.Escape(name.Trim()).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
            // HKLM 만 본다 — 자동 확인(사용자)과 서비스(SYSTEM)는 HKCU 가 서로 달라, HKCU 로 판단하면 둘이 다르게 본다
            var hives = new[] { RegistryHive.LocalMachine };
            foreach (var hive in hives)
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var hk = RegistryKey.OpenBaseKey(hive, view))
                    using (var un = hk.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (un == null) continue;
                        foreach (var sub in un.GetSubKeyNames())
                        {
                            using (var k = un.OpenSubKey(sub))
                            {
                                var dn = k == null ? null : k.GetValue("DisplayName") as string;
                                if (dn == null) continue;
                                // 제어판에 안 보이는 하위 항목은 뺀다 — 예: .NET 런타임은 묶음(9.0.23.36622) 아래 MSI 들이 72.92.x 같은
                                // 내부 판 번호로 따로 적혀 있어, 그것을 읽으면 판 비교가 틀린다
                                if (k.GetValue("SystemComponent") is int && (int)k.GetValue("SystemComponent") == 1) continue;
                                dn = dn.Trim();
                                if (!rx.IsMatch(dn)) continue;
                                found = true;
                                var v = k.GetValue("DisplayVersion") as string;
                                if (!string.IsNullOrEmpty(v) && (best == null || Core.CompareVersions(v, best) > 0)) best = v;
                            }
                        }
                    }
                }
            }
            return found ? (best ?? "설치됨") : null;
        }

        // ------------------------------------------------------------ 프린터 연결 상태
        public class PrinterInfo { public string Name, Driver, Port; public bool Offline; }

        public static List<PrinterInfo> Printers()
        {
            var list = new List<PrinterInfo>();
            var live = LiveUsbPorts();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DriverName, PortName, WorkOffline FROM Win32_Printer"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var p = new PrinterInfo();
                        p.Name = o["Name"] as string;
                        p.Driver = o["DriverName"] as string;
                        p.Port = o["PortName"] as string;
                        // USB 포트면 '지금 그 포트에 장치가 꽂혀 있나' 로 본다 — 드라이버마다 '오프라인' 표시를 하는 곳 · 안 하는 곳이
                        // 있어서(IDP 는 뽑아도 온라인으로 남는다) Win32_Printer.WorkOffline 만으로는 틀린다
                        if (p.Port != null && p.Port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && live != null)
                            p.Offline = !live.Contains(p.Port.ToUpperInvariant());
                        else
                            p.Offline = o["WorkOffline"] is bool && (bool)o["WorkOffline"];
                        list.Add(p);
                    }
                }
            }
            catch (Exception e) { Core.Log("프린터 목록 실패: " + e.Message); }
            return list;
        }

        /// <summary>지금 장치가 꽂혀 있는 USB 인쇄 포트 (예: USB006). USB 인쇄 장치 인터페이스의 '#\Control' 키는 장치가 연결돼
        /// 있을 때만 생긴다 (Linked=1). 못 읽으면 null.</summary>
        public static HashSet<string> LiveUsbPorts()
        {
            try
            {
                var set = new HashSet<string>();
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{28d78fad-5a12-11d1-ae5b-0000f803a8c2}"))
                {
                    if (cls == null) return set;
                    foreach (var name in cls.GetSubKeyNames())
                    {
                        using (var dp = cls.OpenSubKey(name + @"\#\Device Parameters"))
                        using (var ctl = cls.OpenSubKey(name + @"\#\Control"))
                        {
                            if (dp == null || ctl == null) continue;
                            object linked = ctl.GetValue("Linked");
                            if (!(linked is int) || (int)linked == 0) continue;
                            string bas = dp.GetValue("Base Name") as string ?? "USB";
                            object num = dp.GetValue("Port Number");
                            if (num is int) set.Add((bas + ((int)num).ToString("000")).ToUpperInvariant());
                        }
                    }
                }
                return set;
            }
            catch (Exception e) { Core.Log("USB 포트 확인 실패: " + e.Message); return null; }
        }

        /// <summary>최근 10분 안에 들어온 인쇄 작업 수 (자동 업데이트는 인쇄 중이면 다음으로 미룬다). 못 읽으면 0.
        /// 오래된 작업은 세지 않는다 — 프린터가 꺼져 있어 몇 주째 걸려 있는 작업 때문에 업데이트가 영영 밀리지 않게.</summary>
        public static int PrintJobs()
        {
            try
            {
                int n = 0;
                var since = DateTime.Now.AddMinutes(-10);
                using (var s = new ManagementObjectSearcher("SELECT TimeSubmitted FROM Win32_PrintJob"))
                using (var r = s.Get())
                {
                    foreach (ManagementObject o in r)
                    {
                        using (o)
                        {
                            var ts = o["TimeSubmitted"] as string;
                            DateTime t;
                            try { t = ManagementDateTimeConverter.ToDateTime(ts); } catch { t = DateTime.Now; }   // 시각을 못 읽으면 지금 것으로 본다
                            if (t >= since) n++;
                        }
                    }
                }
                return n;
            }
            catch (Exception e) { Core.Log("인쇄 작업 확인 실패: " + e.Message); return 0; }
        }

        // ------------------------------------------------------------ 할 일
        public class Pending
        {
            public string Id;        // catalog id, "manager", "coredotprinter"
            public string Title;
            public string From, To;  // From == null 이면 새로 추가
            public bool IsNew { get { return From == null; } }
        }

        /// <summary>깔려 있는 항목이 requires 로 적은 것 중 빠진 것 (예: SMART Utility 는 깔렸는데 .NET 9 가 없음).
        /// 이것이 없으면 깔린 프로그램이 제대로 안 돌아 (.NET 설치하라는 창이 계속 뜬다) — 묻지 않고 채운다.</summary>
        public static List<Pending> MissingRequires(CatalogData cat)
        {
            var list = new List<Pending>();
            if (cat == null) return list;
            var added = new HashSet<string>();
            foreach (var it in cat.Items)
            {
                if (it.Requires.Count == 0 || InstalledVersion(it) == null) continue;
                foreach (var rid in it.Requires)
                {
                    var r = cat.Find(rid);
                    if (r == null || string.IsNullOrEmpty(r.Url) || added.Contains(r.Id) || InstalledVersion(r) != null) continue;
                    added.Add(r.Id);
                    list.Add(new Pending { Id = r.Id, Title = r.Name + " (" + it.Name + " 에 필요)", To = r.Version });
                }
            }
            return list;
        }

        public static List<Pending> ComputePending(CatalogData cat, ReleaseInfo printer, string managerVersion)
        {
            var list = new List<Pending>();
            if (cat != null)
            {
                if (!string.IsNullOrEmpty(cat.ManagerVersion) && !string.IsNullOrEmpty(cat.ManagerUrl)
                    && Core.CompareVersions(cat.ManagerVersion, managerVersion) > 0)
                    list.Add(new Pending { Id = "manager", Title = "코어닷 관리센터", From = managerVersion, To = cat.ManagerVersion });
                foreach (var it in cat.Items)
                {
                    if (string.IsNullOrEmpty(it.Url)) continue;
                    string have = InstalledVersion(it);
                    if (have == null) list.Add(new Pending { Id = it.Id, Title = it.Name, To = it.Version });
                    else if (have != "설치됨" && Core.CompareVersions(have, it.Version) < 0)
                        list.Add(new Pending { Id = it.Id, Title = it.Name, From = have, To = it.Version });
                }
            }
            if (printer != null)
            {
                try
                {
                    if (!Core.IsUpToDate(printer))
                    {
                        string have = Core.InstalledVersion();
                        string to = printer.Tag;
                        if (have != null && have == printer.Tag) to = printer.Tag + " (수정판)";
                        list.Add(new Pending { Id = "coredotprinter", Title = "코어닷프린터", From = have, To = to });
                    }
                }
                catch (Exception e) { Core.Log("코어닷프린터 확인 실패: " + e.Message); }
            }
            return list;
        }
    }
}

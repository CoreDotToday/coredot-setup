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

    public class Item
    {
        public DriverFamily Replaces;              // exe: 처음 깔 때 먼저 지울 같은 제조사 드라이버 (예: Evolis)
        public string Id, Name, Kind, Version, Url, Sha256;
        public long Size;
        public string Inf;                         // driver: 드라이버 저장소의 원래 INF 이름 (예: e1wf1b0e.inf)
        public string[] Printers = new string[0];  // driver: 인쇄 드라이버 이름들 (Add-PrinterDriver)
        public string Args;                        // exe: 조용히 설치 인자
        public string Detect;                      // exe: 제어판 프로그램 목록의 이름
        public string Open;                        // exe: [열기] 로 띄울 파일
        public List<int> RebootCodes = new List<int>();   // exe: '설치됨 · 재부팅 필요' 로 볼 종료 코드 (예: Evolis 500)
        public List<Tool> Tools = new List<Tool>();
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
                    it.Open = Expand(it.Open);
                    it.Args = Expand(it.Args);   // 예: /LOG="%ProgramData%\CoreDot\logs\..."
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
            bool prefix = name.EndsWith("*");
            if (prefix) name = name.TrimEnd('*').Trim();
            var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
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
                                dn = dn.Trim();
                                if (prefix ? !dn.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                           : !dn.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
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

        // ------------------------------------------------------------ 할 일
        public class Pending
        {
            public string Id;        // catalog id, "manager", "coredotprinter"
            public string Title;
            public string From, To;  // From == null 이면 새로 추가
            public bool IsNew { get { return From == null; } }
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

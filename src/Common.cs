// 공용: 로그 · 다운로드 · 해시 · 코어닷프린터(GitHub 릴리스) 설치 · 교체
// C# 5 — 윈도우에 기본으로 있는 .NET Framework 4 csc 로 빌드한다 (따로 설치할 것 없음)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CoreDot
{
    public class ReleaseInfo
    {
        public string Tag;
        public string Notes;
        public string PublishedAt;
        public string AssetName;
        public string AssetUpdatedAt;
        public string Url;
        public long Size;
        public string Sha256;   // GitHub 이 주는 자산 digest
    }

    public static class Core
    {
        public const string PrinterRepo = "CoreDotToday/coredot-printer-v2";
        public const string AppDir = @"C:\CoreDotPrinter";        // 코어닷프린터 exe 자리 (Users 쓰기 가능 — 사용자 권한으로 교체)
        public const string ExeName = "CoreDotPrinter.exe";
        public const string StateName = "version.txt";
        public const string UserAgent = "CoreDotManager";

        public static string LogFile;

        static Core()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
        }

        // ---------------------------------------------------------------- 로그
        static readonly object logLock = new object();

        public static void Log(string msg)
        {
            try
            {
                if (LogFile == null) return;
                lock (logLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        File.Copy(LogFile, LogFile + ".1", true);
                        File.Delete(LogFile);
                    }
                    File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- HTTP · JSON
        public static string GetText(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.Headers["Cache-Control"] = "no-cache";
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static Dictionary<string, object> ParseJson(string json)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return ser.Deserialize<Dictionary<string, object>>(json);
        }

        public static string ToJson(object o)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return ser.Serialize(o);
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        public static long Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }

        /// <summary>받아서 크기 · SHA256 을 확인한다. 이미 같은 파일이 있으면 다시 받지 않는다. 틀리면 지우고 예외.</summary>
        public static void DownloadFile(string url, long size, string sha256, string dest, Action<long, long> progress)
        {
            if (File.Exists(dest) && Matches(dest, size, sha256))
            {
                Log("이미 받아 둔 파일 사용: " + dest);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            string part = dest + ".part";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write))
            {
                long total = resp.ContentLength > 0 ? resp.ContentLength : size;
                long done = 0, last = 0;
                var buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    if (progress != null && (done - last > 512 * 1024 || done == total))
                    {
                        progress(done, total);
                        last = done;
                    }
                }
            }
            if (!Matches(part, size, sha256))
            {
                TryDelete(part);
                throw new Exception("받은 파일이 손상되었습니다 (크기 또는 SHA256 불일치) — " + Path.GetFileName(dest));
            }
            TryDelete(dest);
            File.Move(part, dest);
            Log("다운로드 · 검증 통과: " + Path.GetFileName(dest));
        }

        static bool Matches(string file, long size, string sha256)
        {
            var fi = new FileInfo(file);
            if (size > 0 && fi.Length != size) return false;
            if (!string.IsNullOrEmpty(sha256) && Sha256Of(file) != sha256.ToLowerInvariant()) return false;
            return true;
        }

        public static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var h = sha.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (Version.TryParse(Pad(a), out va) && Version.TryParse(Pad(b), out vb)) return va.CompareTo(vb);
            return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        static string Pad(string v)
        {
            if (v == null) return null;
            v = v.Trim().TrimStart('v', 'V');
            return v.IndexOf('.') < 0 ? v + ".0" : v;
        }

        // ---------------------------------------------------------------- 코어닷프린터 (공개 GitHub 릴리스)
        public static ReleaseInfo GetLatestPrinter()
        {
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + PrinterRepo + "/releases/latest");
            req.UserAgent = UserAgent;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 20000;
            string json;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = sr.ReadToEnd();
            var o = ParseJson(json);
            var r = new ReleaseInfo();
            r.Tag = Str(o, "tag_name");
            r.Notes = Str(o, "body");
            r.PublishedAt = Str(o, "published_at");
            var assets = o.ContainsKey("assets") ? o["assets"] as IEnumerable : null;
            if (assets != null)
            {
                foreach (object ao in assets)
                {
                    var a = ao as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Str(a, "name") ?? "";
                    if (!name.StartsWith("CoreDotPrinter", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    r.AssetName = name;
                    r.Url = Str(a, "browser_download_url");
                    r.AssetUpdatedAt = Str(a, "updated_at");
                    r.Size = Num(a, "size");
                    string digest = Str(a, "digest");
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        r.Sha256 = digest.Substring(7).ToLowerInvariant();
                    break;
                }
            }
            if (r.Url == null) throw new Exception("코어닷프린터 최신 릴리스(" + r.Tag + ")에 exe 파일이 없습니다.");
            return r;
        }

        public static Dictionary<string, string> ReadState(string dir)
        {
            var d = new Dictionary<string, string>();
            try
            {
                foreach (var line in File.ReadAllLines(Path.Combine(dir, StateName), Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        public static void WriteState(string dir, ReleaseInfo r, string sha)
        {
            var sb = new StringBuilder();
            sb.AppendLine("tag=" + r.Tag);
            sb.AppendLine("sha256=" + sha);
            sb.AppendLine("asset=" + r.AssetName);
            sb.AppendLine("asset_updated_at=" + r.AssetUpdatedAt);
            sb.AppendLine("installed_at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            File.WriteAllText(Path.Combine(dir, StateName), sb.ToString(), Encoding.UTF8);
        }

        /// <summary>설치된 exe 가 최신 릴리스 파일과 같은가 — 해시로 비교 (같은 번호로 다시 낸 판도 잡는다).</summary>
        public static bool IsUpToDate(ReleaseInfo r)
        {
            string exe = Path.Combine(AppDir, ExeName);
            if (!File.Exists(exe)) return false;
            if (r.Sha256 != null) return Sha256Of(exe) == r.Sha256;
            var st = ReadState(AppDir);
            string tag, upd;
            return st.TryGetValue("tag", out tag) && tag == r.Tag
                && st.TryGetValue("asset_updated_at", out upd) && upd == r.AssetUpdatedAt;
        }

        /// <summary>설치된 코어닷프린터 판. 없으면 null.</summary>
        public static string InstalledVersion()
        {
            string exe = Path.Combine(AppDir, ExeName);
            if (!File.Exists(exe)) return null;
            var st = ReadState(AppDir);
            string tag, sha;
            if (st.TryGetValue("tag", out tag) && st.TryGetValue("sha256", out sha))
            {
                try { if (Sha256Of(exe) == sha) return tag; } catch { }
            }
            try
            {
                string v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!string.IsNullOrEmpty(v)) return Regex.Replace(v.Trim(), @"\.0$", "");
            }
            catch { }
            return "알 수 없음";
        }

        // 릴리스 exe 는 Nuitka onefile — 바깥 exe(CoreDotPrinter*.exe)가 풀어 둔 '프린터서버.exe' 를 띄운다
        public static List<Process> FindAppProcesses()
        {
            var list = new List<Process>();
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                if (p.Id == me) continue;
                string n = p.ProcessName;
                if (n.StartsWith("CoreDotPrinter", StringComparison.OrdinalIgnoreCase) || n == "프린터서버")
                    list.Add(p);
            }
            return list;
        }

        public static bool StopApp(int graceMs)
        {
            var procs = FindAppProcesses();
            if (procs.Count == 0) return false;
            Log("실행 중인 코어닷프린터 종료: " + procs.Count + "개 프로세스");
            foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
            var until = DateTime.Now.AddMilliseconds(graceMs);
            while (DateTime.Now < until && FindAppProcesses().Count > 0) Thread.Sleep(500);
            foreach (var p in FindAppProcesses())
            {
                try { p.Kill(); p.WaitForExit(5000); } catch (Exception e) { Log("강제 종료 실패: " + p.ProcessName + " " + e.Message); }
            }
            Thread.Sleep(1000);
            return true;
        }

        public static void StartApp(bool fromElevated)
        {
            string exe = Path.Combine(AppDir, ExeName);
            if (!File.Exists(exe)) return;
            if (fromElevated) Process.Start("explorer.exe", "\"" + exe + "\"");   // 관리자 권한을 물려주지 않게
            else
            {
                var psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = AppDir;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            Log("코어닷프린터 실행: " + exe);
        }

        /// <summary>받아 둔 파일로 exe 를 바꾼다. 실패하면 이전 exe 로 되돌린다. 켜져 있었으면 true.</summary>
        public static bool ApplyUpdate(ReleaseInfo r, string downloaded, Action<string> status)
        {
            string exe = Path.Combine(AppDir, ExeName);
            string bak = exe + ".bak";
            if (status != null) status("코어닷프린터를 닫는 중…");
            bool wasRunning = StopApp(15000);
            if (status != null) status("새 버전으로 바꾸는 중…");
            Exception last = null;
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (File.Exists(exe)) File.Replace(downloaded, exe, bak, true);
                    else File.Move(downloaded, exe);
                    last = null;
                    break;
                }
                catch (Exception e)
                {
                    last = e;
                    Thread.Sleep(1000);
                    if (i == 5) StopApp(3000);
                }
            }
            if (last != null)
            {
                if (!File.Exists(exe) && File.Exists(bak)) { try { File.Copy(bak, exe); } catch { } }
                Log("교체 실패: " + last.Message);
                if (wasRunning) { try { StartApp(false); } catch { } }
                throw new Exception("파일을 바꾸지 못했습니다: " + last.Message);
            }
            WriteState(AppDir, r, r.Sha256 ?? Sha256Of(exe));
            Log("코어닷프린터 업데이트 적용: " + r.Tag);
            return wasRunning;
        }

        /// <summary>코어닷프린터를 최신으로 (받기 → 닫기 → 교체 → 켜져 있었으면 다시 켜기). 이미 최신이면 false.</summary>
        public static bool UpdatePrinterApp(ReleaseInfo r, bool fromElevated, Action<string> status)
        {
            if (IsUpToDate(r)) return false;
            string dl = Path.Combine(AppDir, ExeName + ".download");
            DownloadFile(r.Url, r.Size, r.Sha256, dl, delegate(long d, long t)
            {
                if (status != null) status("코어닷프린터 받는 중 " + Mb(d) + " / " + Mb(t));
            });
            bool was = ApplyUpdate(r, dl, status);
            if (was) StartApp(fromElevated);
            return true;
        }

        public static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + "MB";
        }

        public static string PlainNotes(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            string s = md.Replace("**", "").Replace("`", "");
            s = Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^)]+)\)", "$1");
            return s.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }
}

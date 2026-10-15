using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MousePinch
{
    /// <summary>settings.ini in %APPDATA%\MousePinch: key=value lines.</summary>
    public class Settings
    {
        public bool Enabled = true;
        public double ZoomPerNotch = 1.2;
        public bool InvertPan;
        public bool Momentum;
        public string[] Apps = { "chrome.exe", "msedge.exe", "brave.exe" };

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MousePinch"); }
        }
        public static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.StartsWith("#") || eq < 1) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    double d;
                    switch (k)
                    {
                        case "enabled": s.Enabled = v != "0" && v.ToLowerInvariant() != "false"; break;
                        case "zoom_per_notch":
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 1.01 && d < 3)
                                s.ZoomPerNotch = d;
                            break;
                        case "invert_pan": s.InvertPan = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "momentum": s.Momentum = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "apps":
                            var list = new List<string>();
                            foreach (var a in v.Split(',')) if (a.Trim().Length > 0) list.Add(a.Trim().ToLowerInvariant());
                            if (list.Count > 0) s.Apps = list.ToArray();
                            break;
                    }
                }
            }
            catch (Exception e) { Log.Write("settings: " + e.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    "# Mouse Pinch settings (also changed from the tray menu)",
                    "enabled=" + (Enabled ? "1" : "0"),
                    "# page zoom per wheel notch while Ctrl is held",
                    "zoom_per_notch=" + ZoomPerNotch.ToString(CultureInfo.InvariantCulture),
                    "# 0: the page follows the mouse (grab), 1: it moves the other way",
                    "invert_pan=" + (InvertPan ? "1" : "0"),
                    "# 1: the page keeps gliding if you let go of Ctrl while moving",
                    "momentum=" + (Momentum ? "1" : "0"),
                    "# programs where Ctrl + wheel pinches (Chromium browsers)",
                    "apps=" + string.Join(",", Apps),
                });
            }
            catch (Exception e) { Log.Write("settings save: " + e.Message); }
        }
    }

    public static class Log
    {
        public static bool ToConsole;
        static readonly object gate = new object();

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg;
            lock (gate)
            {
                if (ToConsole) { Console.WriteLine(line); return; }
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    string path = Path.Combine(Settings.Dir, "mousepinch.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024) File.Delete(path);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}

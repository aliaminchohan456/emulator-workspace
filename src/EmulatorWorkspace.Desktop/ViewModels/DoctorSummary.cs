using System;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-07 fix pass: the readings the Settings doctor block prints as label/value
/// rows. Two of the supervisor's raw fields are not readable on their own:
///
/// - the acceleration verdict is the engine's own multi-line probe, joined with
///   " | " ("accel: | 0 | WHPX(10.0.26300) is installed and usable."), and
/// - the pinned image is a package id ("system-images;android-35;default;x86_64").
///
/// Both are mapped to the same reading names the Inspector uses, with the raw
/// text kept for the row's tooltip and for the copy button. Pure, so the tests
/// can pin the mapping without a WPF window.
/// </summary>
public static class DoctorSummary
{
    /// <summary>
    /// "WHPX 10.0.26300" when the engine probe named the Windows Hypervisor
    /// Platform and the supervisor says it is usable; otherwise the raw verdict
    /// exactly as reported, with no chip. A friendly name is never printed for a
    /// probe that did not pass.
    /// </summary>
    public static (string Label, bool Ready) Acceleration(string? raw, bool whpxUsable)
    {
        var text = (raw ?? "").Trim();
        if (!whpxUsable)
        {
            return (text.Length == 0 ? "Not reported" : text, false);
        }

        var version = WhpxVersion(text);
        return version.Length == 0
            ? (text.Length == 0 ? "Not reported" : text, false)
            : ("WHPX " + version, true);
    }

    /// <summary>The text between "WHPX(" and ")" anywhere in the verdict.</summary>
    public static string WhpxVersion(string? raw)
    {
        var text = raw ?? "";
        var open = text.IndexOf("WHPX(", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return "";
        }

        var start = open + "WHPX(".Length;
        var close = text.IndexOf(')', start);
        return close <= start ? "" : text[start..close].Trim();
    }

    /// <summary>
    /// The pinned image as the Inspector names it, plus the pinned revision the
    /// supervisor verified. An uninstalled pin keeps the friendly name and says
    /// "rev missing" rather than pretending the image is there.
    /// </summary>
    public static string Image(string? package, string? revision, string? sizeSuffix)
    {
        var name = ImageNaming.Describe(package);
        if (name.Length == 0)
        {
            return "Not reported";
        }

        var rev = (revision ?? "").Trim();
        var suffix = sizeSuffix ?? "";
        var parts = new System.Collections.Generic.List<string>(3) { name };
        if (rev.Length > 0)
        {
            parts.Add("rev " + rev);
        }

        return suffix.Length > 0
            ? string.Join(" · ", parts) + suffix
            : string.Join(" · ", parts);
    }

    /// <summary>"name · driver version", with the missing half dropped.</summary>
    public static string Gpu(string? name, string? driver)
    {
        var gpu = (name ?? "").Trim();
        var drv = (driver ?? "").Trim();
        if (gpu.Length == 0 && drv.Length == 0)
        {
            return "Not reported";
        }

        if (drv.Length == 0)
        {
            return gpu;
        }

        return gpu.Length == 0 ? "driver " + drv : gpu + " · driver " + drv;
    }
}

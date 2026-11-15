using System;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// The friendly reading names built from a real system-image package id. The
/// Inspector and the Settings doctor block both print these, and the library
/// cards read the same parts, so the text lives in one place.
///
/// It is pure (no WPF) so the Settings doctor tests can compile it directly.
/// </summary>
public static class ImageNaming
{
    /// <summary>API level to the marketing Android version.</summary>
    public static string AndroidVersion(int api) => api switch
    {
        35 => "Android 15",
        34 => "Android 14",
        33 => "Android 13",
        32 => "Android 12L",
        31 => "Android 12",
        30 => "Android 11",
        29 => "Android 10",
        28 => "Android 9",
        > 0 => $"API {api}",
        _ => "Android version not reported",
    };

    /// <summary>Splits "system-images;android-35;default;x86_64" into its parts.</summary>
    public static (int Api, string Abi, string Tag) ParseImage(string? package)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return (0, "", "");
        }

        var parts = package.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var api = 0;
        var abi = "";
        var tag = "";
        foreach (var part in parts)
        {
            if (part.StartsWith("android-", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part["android-".Length..], out var parsed))
            {
                api = parsed;
            }
            else if (part is "default" or "google_apis" or "google_apis_playstore" or "aosp_atd" or "google_atd")
            {
                tag = part;
            }
            else if (part is "x86_64" or "x86" or "arm64-v8a" or "armeabi-v7a" or "riscv64")
            {
                abi = part;
            }
        }

        return (api, abi, tag);
    }

    /// <summary>A one-word reading name for an image tag.</summary>
    public static string TagLabel(string tag) => tag switch
    {
        "default" => "AOSP",
        "google_apis" => "Google APIs",
        "google_apis_playstore" => "Play Store",
        "aosp_atd" => "AOSP ATD",
        "google_atd" => "Google ATD",
        _ => tag.Length > 0 ? tag : "image",
    };

    /// <summary>
    /// "Android 15 · AOSP · x86_64" for a package the tables recognise, the raw
    /// package when they do not, and an empty string for no package at all. The
    /// caller decides what an empty result means in its own row.
    /// </summary>
    public static string Describe(string? package)
    {
        var raw = (package ?? "").Trim();
        if (raw.Length == 0)
        {
            return "";
        }

        var parsed = ParseImage(raw);
        var label = parsed.Tag.Length > 0 ? TagLabel(parsed.Tag) : RawTag(raw);

        var parts = new System.Collections.Generic.List<string>(3);
        if (parsed.Api > 0)
        {
            parts.Add(AndroidVersion(parsed.Api));
        }

        if (label.Length > 0)
        {
            parts.Add(label);
        }

        if (parsed.Abi.Length > 0)
        {
            parts.Add(parsed.Abi);
        }

        return parts.Count == 0 ? raw : string.Join(" · ", parts);
    }

    /// <summary>
    /// The tag segment of "system-images;android-35;google_apis_go;x86_64" when
    /// the tables do not name it: the segment that is not the root prefix, not
    /// the android-NN level and not an ABI.
    /// </summary>
    public static string RawTag(string package)
    {
        foreach (var part in package.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("system-images", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("android-", StringComparison.OrdinalIgnoreCase)
                || part is "x86_64" or "x86" or "arm64-v8a" or "armeabi-v7a" or "riscv64")
            {
                continue;
            }

            return part;
        }

        return "";
    }
}

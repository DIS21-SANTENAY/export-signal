using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ExportSignal;

/// <summary>Petites fonctions communes : dates, tailles, noms de fichiers, lecture du JSON.</summary>
static class Outils
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    // Bornes acceptées par DateTimeOffset, pour ne jamais planter sur un horodatage aberrant.
    const long MsMin = -62_135_596_800_000 + 86_400_000;
    const long MsMax = 253_402_300_799_999 - 86_400_000;

    public static DateTime Local(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, MsMin, MsMax)).LocalDateTime;

    public static long VersMs(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    public static string Nombre(long n) => n.ToString("N0", Fr);

    public static string Pluriel(long n, string singulier, string pluriel) =>
        Nombre(n) + " " + (n > 1 ? pluriel : singulier);

    public static string Taille(long octets)
    {
        if (octets < 1024) return octets + " octets";
        double v = octets / 1024.0;
        if (v < 1024) return v.ToString("0", Fr) + " Ko";
        v /= 1024;
        if (v < 1024) return v.ToString(v < 10 ? "0.0" : "0", Fr) + " Mo";
        v /= 1024;
        return v.ToString("0.0", Fr) + " Go";
    }

    public static string JourLong(DateTime d) => d.ToString("dddd d MMMM yyyy", Fr);
    public static string JourCourt(DateTime d) => d.ToString("dd/MM/yyyy", Fr);
    public static string Heure(DateTime d) => d.ToString("HH:mm", Fr);

    public static string Duree(long secondes)
    {
        if (secondes % 604800 == 0) return Pluriel(secondes / 604800, "semaine", "semaines");
        if (secondes % 86400 == 0) return Pluriel(secondes / 86400, "jour", "jours");
        if (secondes % 3600 == 0) return Pluriel(secondes / 3600, "heure", "heures");
        if (secondes % 60 == 0) return Pluriel(secondes / 60, "minute", "minutes");
        return Pluriel(secondes, "seconde", "secondes");
    }

    static readonly HashSet<string> NomsReserves = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Transforme un texte quelconque en nom de fichier ou de dossier accepté par Windows.</summary>
    public static string NomSur(string? nom, int max = 60, string defaut = "Sans nom")
    {
        var sb = new StringBuilder();
        foreach (var c in (nom ?? "").Normalize(NormalizationForm.FormC))
        {
            if (c < 32 || "<>:\"/\\|?*".Contains(c)) { sb.Append(' '); continue; }
            // Les emoji et caractères invisibles compliquent la vie des autres logiciels : on les retire.
            if (char.IsSurrogate(c) || c == '️' || c == '‍' || c == '￼') continue;
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.OtherSymbol or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                or UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }
        var propre = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (propre.Length > max) propre = propre[..max];
        propre = propre.Trim(' ', '.');
        if (propre.Length == 0) propre = defaut;
        if (NomsReserves.Contains(propre)) propre = "_" + propre;
        return propre;
    }

    /// <summary>Rend un nom unique dans un ensemble, en ajoutant « (2) », « (3) » au besoin.</summary>
    public static string Unique(HashSet<string> pris, string racine, string extension = "")
    {
        var nom = racine + extension;
        for (int n = 2; !pris.Add(nom); n++) nom = racine + " (" + n + ")" + extension;
        return nom;
    }

    // ---- Lecture tolérante du JSON de Signal : un champ absent ou d'un autre type donne null.

    public static string? Txt(this JsonElement e, string nom) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static long? Entier(this JsonElement e, string nom) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d) ? (long)d : null;

    public static bool Vrai(this JsonElement e, string nom) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement? Objet(this JsonElement e, string nom) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : null;

    public static IEnumerable<JsonElement> Liste(this JsonElement e, string nom)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray()) yield return x;
    }
}

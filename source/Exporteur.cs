using System.Security.Cryptography;
using System.Text;

namespace ExportSignal;

/// <summary>
/// Mène l'export : pour chaque conversation choisie, lit les messages, remet les fichiers en clair
/// et écrit les pages web, le texte et le JSON demandés. Un journal résume ce qui a été fait.
/// </summary>
sealed class Exporteur
{
    /// <summary>Au-delà de ce nombre de lignes, une conversation est découpée en une page par année.</summary>
    const int SeuilDecoupe = 4000;

    readonly BaseSignal _base;
    readonly Options _o;
    readonly IProgress<Avancement>? _progres;
    readonly CancellationToken _arret;
    readonly Interprete _interprete;
    readonly Bilan _bilan = new();
    readonly DateTime _maintenant = DateTime.Now;
    readonly List<string> _journal = new();

    public Exporteur(BaseSignal source, Options options, IProgress<Avancement>? progres, CancellationToken arret)
    {
        _base = source;
        _o = options;
        _progres = progres;
        _arret = arret;
        _interprete = new Interprete(source);
    }

    public Bilan Executer()
    {
        Directory.CreateDirectory(_o.Destination);
        _bilan.Index = Path.Combine(_o.Destination, "index.html");
        _bilan.Journal = Path.Combine(_o.Destination, "journal.txt");

        long depuis = _o.Depuis is { } a ? Outils.VersMs(a.Date) : long.MinValue;
        long avant = _o.Jusqu is { } b ? Outils.VersMs(b.Date.AddDays(1)) : long.MaxValue;

        var resumes = new List<Resume>();
        var dossiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.html", "journal.txt" };
        long total = Math.Max(1, _o.Conversations.Sum(c => c.Lignes));
        long faits = 0;

        try
        {
            foreach (var conv in _o.Conversations.OrderBy(c => c.Nom, StringComparer.Create(Outils.Fr, true)))
            {
                _arret.ThrowIfCancellationRequested();
                var resume = ExporterConversation(conv, dossiers, depuis, avant, faits, total);
                faits += conv.Lignes;
                if (resume != null) resumes.Add(resume);
            }
        }
        catch (OperationCanceledException)
        {
            _bilan.Annule = true;
            _journal.Add("Export interrompu à la demande de l'utilisateur.");
        }

        if (resumes.Count > 0) SortieHtml.EcrireIndex(_bilan.Index, resumes, _maintenant, _o.Html);
        _bilan.Conversations = resumes.Count;
        EcrireJournal();
        _progres?.Report(new Avancement(1, _bilan.Annule ? "Export interrompu." : "Export terminé."));
        return _bilan;
    }

    Resume? ExporterConversation(Conversation conv, HashSet<string> dossiers, long depuis, long avant, long faits, long total)
    {
        _progres?.Report(new Avancement((double)faits / total, conv.Nom));

        var dates = _base.Horodatages(conv.Id, depuis, avant);
        if (dates.Count == 0)
        {
            _journal.Add(conv.Nom + " : aucun message dans la période demandée, conversation ignorée.");
            return null;
        }

        var resume = new Resume
        {
            Conv = conv,
            Dossier = Outils.Unique(dossiers, Outils.NomSur(conv.Nom, 60, "Conversation")),
            Lignes = dates.Count,
            Messages = dates.Count(d => d.Message),
            Premier = Outils.Local(dates[0].Date),
            Dernier = Outils.Local(dates[^1].Date),
            ExporteLe = _maintenant,
            Dates = dates.Select(d => d.Date).ToHashSet(),
            Annees = dates.GroupBy(d => Outils.Local(d.Date).Year).OrderBy(g => g.Key).Select(g => (g.Key, g.Count(d => d.Message))).ToList(),
        };
        resume.Decoupe = dates.Count > SeuilDecoupe && resume.Annees.Count > 1;

        var dossier = Path.Combine(_o.Destination, resume.Dossier);
        Directory.CreateDirectory(dossier);
        var pieces = _base.PiecesJointes(conv.Id);
        var noms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long fichiersAvant = _bilan.Fichiers, absentsAvant = _bilan.FichiersAbsents, erreursAvant = _bilan.Erreurs;

        var html = _o.Html ? new SortieHtml(dossier, resume) : null;
        var texte = _o.Texte ? new SortieTexte(dossier, resume) : null;
        var json = _o.Json ? new SortieJson(dossier, resume) : null;
        try
        {
            long n = 0;
            foreach (var brut in _base.Messages(conv.Id, depuis, avant))
            {
                _arret.ThrowIfCancellationRequested();
                var m = _interprete.Lire(brut, conv, pieces);
                if (m.Evenement == null && m.Note == null) ExporterFichiers(m, dossier, noms);
                html?.Ecrire(m);
                texte?.Ecrire(m);
                json?.Ecrire(m);
                _bilan.Messages++;
                if (++n % 100 == 0)
                    _progres?.Report(new Avancement((faits + (double)conv.Lignes * n / dates.Count) / total,
                        conv.Nom + ", " + Outils.Nombre(n) + " sur " + Outils.Nombre(dates.Count)));
            }
        }
        finally
        {
            html?.Dispose();
            texte?.Dispose();
            json?.Dispose();
        }

        var ligne = conv.Nom + " : " + Outils.Pluriel(resume.Lignes, "ligne", "lignes") + " dont "
                    + Outils.Pluriel(resume.Messages, "message", "messages");
        if (_o.Fichiers)
        {
            ligne += ", " + Outils.Pluriel(_bilan.Fichiers - fichiersAvant, "fichier", "fichiers");
            if (_bilan.FichiersAbsents > absentsAvant) ligne += ", " + (_bilan.FichiersAbsents - absentsAvant) + " non disponibles sur cet ordinateur";
            if (_bilan.Erreurs > erreursAvant) ligne += ", " + (_bilan.Erreurs - erreursAvant) + " en erreur";
        }
        _journal.Add(ligne + ".");
        return resume;
    }

    // ------------------------------------------------------------------ fichiers

    void ExporterFichiers(Message m, string dossier, HashSet<string> noms)
    {
        if (!_o.Fichiers) return;
        foreach (var p in m.Fichiers) ExporterFichier(p, m.Date, dossier, noms, "");
        if (m.Autocollant != null) ExporterFichier(m.Autocollant, m.Date, dossier, noms, "_autocollant");
        foreach (var a in m.Apercus)
            if (a.Image != null) ExporterFichier(a.Image, m.Date, dossier, noms, "_apercu");
    }

    void ExporterFichier(PieceJointe p, long date, string dossier, HashSet<string> noms, string suffixe)
    {
        var source = _base.CheminSource(p.Chemin);
        if (source == null)
        {
            p.Absent = true;
            _bilan.FichiersAbsents++;
            return;
        }

        var sousDossier = Path.Combine(dossier, "fichiers");
        Directory.CreateDirectory(sousDossier);
        string nom;
        try
        {
            nom = NomExport(p, date, noms, suffixe, Dechiffrement.Debut(source, p.CleLocale, p.Version));
        }
        catch (Exception e) when (EstErreurDeFichier(e))
        {
            nom = NomExport(p, date, noms, suffixe, Array.Empty<byte>());
        }
        var destination = Path.Combine(sousDossier, nom);
        try
        {
            // Un fichier déjà exporté à la bonne taille n'est pas refait : relancer un export est rapide.
            long attendue = Dechiffrement.TailleAttendue(source, p.CleLocale, p.Version, p.Taille);
            if (!(File.Exists(destination) && new FileInfo(destination).Length == attendue))
                Dechiffrement.VersFichier(source, p.CleLocale, p.Version, p.Taille, destination);
            p.Exporte = "fichiers/" + nom;
            _bilan.Fichiers++;
        }
        catch (Exception e) when (EstErreurDeFichier(e))
        {
            p.Absent = true;
            _bilan.Erreurs++;
            _journal.Add("  Fichier illisible (" + p.TypeContenu + ", " + Outils.JourCourt(Outils.Local(date)) + ") : " + e.Message);
            return;
        }

        // Image fixe d'une vidéo, affichée avant la lecture.
        if (p.Video && _base.CheminSource(p.CheminCapture) is { } capture)
        {
            try
            {
                var format = FormatReel(Dechiffrement.Debut(capture, p.CleCapture, p.VersionCapture)) ?? ".jpg";
                var nomCapture = Path.GetFileNameWithoutExtension(nom) + "_image" + format;
                if (!noms.Add(nomCapture)) return;
                var dest = Path.Combine(sousDossier, nomCapture);
                long attendue = Dechiffrement.TailleAttendue(capture, p.CleCapture, p.VersionCapture, p.TailleCapture);
                if (!(File.Exists(dest) && new FileInfo(dest).Length == attendue))
                    Dechiffrement.VersFichier(capture, p.CleCapture, p.VersionCapture, p.TailleCapture, dest);
                p.CaptureExportee = "fichiers/" + nomCapture;
            }
            catch (Exception e) when (EstErreurDeFichier(e)) { }   // la vidéo reste lisible sans son image fixe
        }
    }

    static bool EstErreurDeFichier(Exception e) =>
        e is IOException or InvalidDataException or CryptographicException or FormatException
            or UnauthorizedAccessException or NotSupportedException;

    /// <summary>Nom du fichier exporté : date et heure du message, puis le nom d'origine s'il existe.</summary>
    static string NomExport(PieceJointe p, long date, HashSet<string> noms, string suffixe, byte[] debut)
    {
        var racine = Outils.Local(date).ToString("yyyy-MM-dd_HHmmss") + suffixe;
        var extension = Extension(p);
        // Le type annoncé par Signal est parfois inexact (PNG annoncé JPEG, AAC brut annoncé MP4) :
        // quand le contenu se reconnaît à ses premiers octets, c'est lui qui décide.
        if (FormatReel(debut) is { } reel && !(reel == ".jpg" && extension == ".jpeg")) extension = reel;
        if (!string.IsNullOrWhiteSpace(p.NomFichier))
        {
            var origine = Outils.NomSur(Path.GetFileNameWithoutExtension(p.NomFichier), 50, "");
            if (origine.Length > 0) racine += "_" + origine;
        }
        return Outils.Unique(noms, racine, extension);
    }

    /// <summary>Extension déduite des premiers octets du fichier, ou null si le format n'est pas reconnu avec certitude.</summary>
    static string? FormatReel(byte[] d)
    {
        bool Commence(params byte[] signature) => d.Length >= signature.Length && d.AsSpan(0, signature.Length).SequenceEqual(signature);
        bool Contient(int position, string texte) =>
            d.Length >= position + texte.Length && System.Text.Encoding.ASCII.GetString(d, position, texte.Length) == texte;

        if (Commence(0xFF, 0xD8, 0xFF)) return ".jpg";
        if (Commence(0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (Contient(0, "GIF8")) return ".gif";
        if (Contient(0, "RIFF") && Contient(8, "WEBP")) return ".webp";
        if (Contient(0, "RIFF") && Contient(8, "WAVE")) return ".wav";
        if (Contient(0, "%PDF")) return ".pdf";
        if (Contient(0, "OggS")) return ".ogg";
        if (Contient(0, "ID3")) return ".mp3";
        // AAC brut (en-tête ADTS) : douze bits à 1, couche à zéro.
        if (d.Length >= 2 && d[0] == 0xFF && (d[1] & 0xF6) == 0xF0) return ".aac";
        return null;   // conteneurs MP4 et autres : on garde l'extension liée au type annoncé
    }

    static string Extension(PieceJointe p)
    {
        // Le type annoncé prime pour les médias : le navigateur s'y fie pour les lire.
        var connue = p.TypeContenu.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/heic" => ".heic",
            "image/bmp" => ".bmp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            "video/3gpp" => ".3gp",
            "audio/aac" => ".m4a",      // Signal range ses messages vocaux « aac » dans un conteneur MP4
            "audio/mp4" => ".m4a",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            "application/pdf" => ".pdf",
            _ => null,
        };
        var origine = Path.GetExtension(p.NomFichier ?? "");
        bool origineValide = origine.Length is > 1 and <= 10 && origine.Skip(1).All(char.IsAsciiLetterOrDigit);
        if (connue != null)
        {
            // « .jpeg » et « .jpg » se valent : on garde l'extension d'origine si elle est du même genre.
            if (origineValide && p.TypeContenu.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                && origine.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpeg";
            return connue;
        }
        if (origineValide) return origine.ToLowerInvariant();
        return p.TypeContenu.ToLowerInvariant() switch
        {
            "text/plain" or "text/x-signal-plain" => ".txt",
            "text/x-vcard" or "text/vcard" => ".vcf",
            "application/zip" => ".zip",
            _ => ".bin",
        };
    }

    // ------------------------------------------------------------------ journal

    void EcrireJournal()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Export Signal du " + Outils.JourCourt(_maintenant) + " à " + Outils.Heure(_maintenant));
        sb.AppendLine("Source : " + _base.Dossier);
        if (_o.Depuis != null || _o.Jusqu != null)
            sb.AppendLine("Période : " + (_o.Depuis is { } a ? "du " + Outils.JourCourt(a) : "depuis le début")
                          + (_o.Jusqu is { } b ? " au " + Outils.JourCourt(b) : " jusqu'à aujourd'hui"));
        sb.AppendLine("Formats : " + string.Join(", ", new[] { _o.Html ? "pages web" : null, _o.Texte ? "texte" : null, _o.Json ? "JSON" : null }.OfType<string>())
                      + (_o.Fichiers ? ", avec les fichiers" : ", sans les fichiers"));
        sb.AppendLine();
        foreach (var ligne in _journal) sb.AppendLine(ligne);
        sb.AppendLine();
        sb.AppendLine("Total : " + Outils.Pluriel(_bilan.Conversations, "conversation", "conversations") + ", "
                      + Outils.Pluriel(_bilan.Messages, "ligne", "lignes") + ", " + Outils.Pluriel(_bilan.Fichiers, "fichier", "fichiers") + ".");
        if (_bilan.FichiersAbsents > 0)
            sb.AppendLine("Fichiers absents de cet ordinateur (jamais téléchargés ou effacés par Signal), non exportés : "
                          + Outils.Nombre(_bilan.FichiersAbsents) + ".");
        if (_bilan.Erreurs > 0) sb.AppendLine(Outils.Pluriel(_bilan.Erreurs, "fichier illisible", "fichiers illisibles") + " (détail plus haut).");
        File.WriteAllText(_bilan.Journal, sb.ToString(), new UTF8Encoding(true));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ExportSignal;

/// <summary>
/// Accès en lecture seule à la base de Signal Desktop.
/// Les conversations et les fichiers de Signal ne sont jamais modifiés, et Signal peut rester ouvert pendant la lecture.
/// (Si Signal est fermé, SQLite peut recréer ses deux fichiers de travail vides, db.sqlite-wal et db.sqlite-shm : c'est sans effet.)
/// </summary>
sealed class BaseSignal : IDisposable
{
    public string Dossier { get; }
    public string DossierFichiers { get; }
    public string? MonAci { get; private set; }
    public string? MonId { get; private set; }
    public List<Conversation> Conversations { get; } = new();

    readonly SqliteConnection _db;
    readonly Dictionary<string, Conversation> _parId = new(StringComparer.Ordinal);
    readonly Dictionary<string, Conversation> _parAci = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, (string Type, string Mode, string Sens, string Etat)>? _appels;

    /// <summary>Dossier de données de Signal Desktop pour la session Windows en cours.</summary>
    public static string DossierParDefaut() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Signal");

    public static bool EstUnDossierSignal(string dossier) =>
        File.Exists(Path.Combine(dossier, "config.json")) && File.Exists(Path.Combine(dossier, "sql", "db.sqlite"));

    public BaseSignal(string dossier)
    {
        Dossier = dossier;
        DossierFichiers = Path.Combine(dossier, "attachments.noindex");
        if (!EstUnDossierSignal(dossier))
            throw new ErreurSignal("Signal Desktop n'a pas été trouvé dans « " + dossier + " ».");

        var cle = LireCle(dossier);
        SQLitePCL.Batteries_V2.Init();
        // Ouverture en lecture seule (mode=ro), au format SQLCipher 4 avec la clé brute de Signal.
        var source = UriFichier(Path.Combine(dossier, "sql", "db.sqlite"))
                     + "?mode=ro&cipher=sqlcipher&legacy=4&key=x%27" + cle + "%27";
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 60,
        }.ToString());
        try
        {
            _db.Open();
            Executer("PRAGMA query_only = ON");
            Scalaire("select count(*) from sqlite_master");
        }
        catch (SqliteException e)
        {
            _db.Dispose();
            throw new ErreurSignal("La base de Signal n'a pas pu être ouverte (" + e.Message + ").", e);
        }
        Charger();
    }

    public void Dispose() => _db.Dispose();

    // ------------------------------------------------------------------ clé de la base

    /// <summary>
    /// Retrouve la clé de la base. Signal la garde dans config.json, chiffrée par une clé maîtresse
    /// (fichier « Local State ») que Windows protège pour la session de l'utilisateur (DPAPI).
    /// </summary>
    static string LireCle(string dossier)
    {
        string? cle;
        try
        {
            using var conf = JsonDocument.Parse(File.ReadAllText(Path.Combine(dossier, "config.json")));
            var chiffree = conf.RootElement.Txt("encryptedKey");
            if (chiffree != null)
            {
                var donnees = Convert.FromHexString(chiffree);
                if (donnees.Length < 3 + 12 + 16 + 1 || Encoding.ASCII.GetString(donnees, 0, 3) != "v10")
                    throw new ErreurSignal("Le format de la clé de Signal n'est pas reconnu par cette version de l'outil.");

                var fEtat = Path.Combine(dossier, "Local State");
                if (!File.Exists(fEtat)) throw new ErreurSignal("Le fichier « Local State » de Signal est introuvable.");
                using var etat = JsonDocument.Parse(File.ReadAllText(fEtat));
                var maitre64 = etat.RootElement.Objet("os_crypt")?.Txt("encrypted_key")
                               ?? throw new ErreurSignal("La clé maîtresse de Signal est introuvable dans « Local State ».");
                var maitre = Convert.FromBase64String(maitre64);
                if (maitre.Length < 6 || Encoding.ASCII.GetString(maitre, 0, 5) != "DPAPI")
                    throw new ErreurSignal("La clé maîtresse de Signal n'est pas au format attendu.");
                try
                {
                    maitre = ProtectedData.Unprotect(maitre[5..], null, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException e)
                {
                    throw new ErreurSignal("Windows refuse de déverrouiller la clé de Signal. "
                        + "L'outil doit être lancé dans la session Windows de la personne qui utilise Signal sur ce poste.", e);
                }
                var clair = new byte[donnees.Length - 3 - 12 - 16];
                try
                {
                    using var gcm = new AesGcm(maitre, 16);
                    gcm.Decrypt(donnees.AsSpan(3, 12), donnees.AsSpan(15, clair.Length), donnees.AsSpan(donnees.Length - 16), clair);
                }
                catch (CryptographicException e)
                {
                    throw new ErreurSignal("La clé de la base de Signal n'a pas pu être déchiffrée : "
                        + "les fichiers « config.json » et « Local State » ne vont pas ensemble ou ont été modifiés.", e);
                }
                cle = Encoding.ASCII.GetString(clair);
            }
            else
            {
                // Anciennes versions de Signal : clé en clair.
                cle = conf.RootElement.Txt("key");
            }
        }
        catch (ErreurSignal) { throw; }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or IOException)
        {
            throw new ErreurSignal("La clé de la base de Signal n'a pas pu être lue (" + e.Message + ").", e);
        }
        if (cle == null || !Regex.IsMatch(cle, "^[0-9a-fA-F]{64}$"))
            throw new ErreurSignal("La clé de la base de Signal est absente ou invalide.");
        return cle;
    }

    /// <summary>Adresse « file: » comprise par SQLite, y compris avec des espaces ou des accents.</summary>
    static string UriFichier(string chemin)
    {
        var morceaux = Path.GetFullPath(chemin).Replace('\\', '/').Split('/');
        var sb = new StringBuilder("file:///");
        for (int i = 0; i < morceaux.Length; i++)
        {
            if (i > 0) sb.Append('/');
            sb.Append(i == 0 && morceaux[i].EndsWith(':') ? morceaux[i] : Uri.EscapeDataString(morceaux[i]));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ requêtes

    SqliteCommand Commande(string sql, params (string Nom, object? Valeur)[] parametres)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (nom, valeur) in parametres) c.Parameters.AddWithValue(nom, valeur ?? DBNull.Value);
        return c;
    }

    void Executer(string sql) { using var c = Commande(sql); c.ExecuteNonQuery(); }

    object? Scalaire(string sql) { using var c = Commande(sql); var v = c.ExecuteScalar(); return v is DBNull ? null : v; }

    bool TableExiste(string nom)
    {
        using var c = Commande("select 1 from sqlite_master where type = 'table' and name = $n", ("$n", nom));
        return c.ExecuteScalar() != null;
    }

    static string? Chaine(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    static long Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
    static int? EntierOuNul(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    // ------------------------------------------------------------------ conversations

    void Charger()
    {
        // Notre propre identifiant : « aci.numéroAppareil ».
        var uuid = Scalaire("select json_extract(json, '$.value') from items where id = 'uuid_id'") as string;
        MonAci = uuid?.Split('.')[0];

        var membres = new Dictionary<string, List<string>>();
        using (var c = Commande("select id, type, name, profileFullName, profileName, e164, serviceId, json from conversations"))
        using (var r = c.ExecuteReader())
        {
            while (r.Read())
            {
                var conv = new Conversation
                {
                    Id = Chaine(r, 0) ?? "",
                    Groupe = Chaine(r, 1) == "group",
                    Numero = Chaine(r, 5),
                    ServiceId = Chaine(r, 6),
                };
                string? surnom = null, carnet = null, identifiant = null;
                try
                {
                    using var doc = JsonDocument.Parse(Chaine(r, 7) ?? "{}");
                    var j = doc.RootElement;
                    surnom = Joindre(j.Txt("nicknameGivenName"), j.Txt("nicknameFamilyName"));
                    carnet = Joindre(j.Txt("systemGivenName"), j.Txt("systemFamilyName"));
                    identifiant = j.Txt("username");
                    if (conv.Groupe)
                        membres[conv.Id] = j.Liste("membersV2").Select(m => m.Txt("aci")).OfType<string>().ToList();
                }
                catch (JsonException) { }

                // Même ordre de priorité que Signal : surnom, nom du carnet, nom de profil, numéro.
                conv.Nom = Premier(surnom, Chaine(r, 2), carnet, Chaine(r, 3), Chaine(r, 4), conv.Numero, identifiant)
                           ?? (conv.Groupe ? "Groupe sans nom" : "Contact inconnu");
                if (!conv.Groupe && MonAci != null && string.Equals(conv.ServiceId, MonAci, StringComparison.OrdinalIgnoreCase))
                {
                    conv.NotesPerso = true;
                    conv.Nom = "Notes à moi-même";
                    MonId = conv.Id;
                }
                Conversations.Add(conv);
                _parId[conv.Id] = conv;
                if (!string.IsNullOrEmpty(conv.ServiceId)) _parAci[conv.ServiceId] = conv;
            }
        }
        foreach (var (id, liste) in membres)
            _parId[id].Membres = liste.Select(a => NomDe(a)).Distinct().OrderBy(n => n, StringComparer.Create(Outils.Fr, true)).ToList();

        // Décomptes par conversation (les stories ne sont pas des conversations).
        using (var c = Commande(@"select conversationId, sum(type is null or type in ('incoming', 'outgoing')), count(*), min(sent_at), max(sent_at)
                                  from messages where storyId is null and type is not 'story' group by conversationId"))
        using (var r = c.ExecuteReader())
        {
            while (r.Read())
            {
                if (!_parId.TryGetValue(Chaine(r, 0) ?? "", out var conv)) continue;
                conv.Messages = Long(r, 1);
                conv.Lignes = Long(r, 2);
                conv.Premier = Long(r, 3);
                conv.Dernier = Long(r, 4);
            }
        }
        if (TableExiste("message_attachments"))
        {
            using var c = Commande(@"select conversationId, count(*), sum(size) from message_attachments
                                     where editHistoryIndex = -1 and path is not null
                                       and attachmentType in ('attachment', 'sticker', 'preview')
                                     group by conversationId");
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                if (!_parId.TryGetValue(Chaine(r, 0) ?? "", out var conv)) continue;
                conv.NombreFichiers = Long(r, 1);
                conv.TailleFichiers = Long(r, 2);
            }
        }
        else
        {
            throw new ErreurSignal("Cette version de Signal Desktop est trop ancienne pour l'outil : mettez Signal à jour puis relancez l'export.");
        }
    }

    static string? Joindre(string? a, string? b)
    {
        var s = ((a ?? "") + " " + (b ?? "")).Trim();
        return s.Length == 0 ? null : s;
    }

    static string? Premier(params string?[] candidats) => candidats.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();

    /// <summary>Nom affiché d'une personne, à partir de son identifiant de conversation ou de son identifiant Signal.</summary>
    public string NomDe(string? cle, string defaut = "Contact inconnu")
    {
        if (string.IsNullOrEmpty(cle)) return defaut;
        if (cle == MonId || string.Equals(cle, MonAci, StringComparison.OrdinalIgnoreCase)) return "Moi";
        if (_parId.TryGetValue(cle, out var c) || _parAci.TryGetValue(cle, out c)) return c.Nom;
        return defaut;
    }

    /// <summary>
    /// Chemin complet d'un fichier de Signal à partir de son chemin relatif, ou null s'il n'existe pas.
    /// Un chemin qui sortirait du dossier des pièces jointes est refusé.
    /// </summary>
    public string? CheminSource(string? relatif)
    {
        if (string.IsNullOrEmpty(relatif) || Path.IsPathRooted(relatif)) return null;
        var racine = Path.GetFullPath(DossierFichiers) + Path.DirectorySeparatorChar;
        var complet = Path.GetFullPath(Path.Combine(racine, relatif));
        return complet.StartsWith(racine, StringComparison.OrdinalIgnoreCase) && File.Exists(complet) ? complet : null;
    }

    // ------------------------------------------------------------------ messages

    // Le « + » devant sent_at est voulu : il empêche SQLite de choisir l'index par date, qui lui ferait
    // parcourir tous les messages de la base pour chaque conversation. Il passe alors par l'index des conversations.
    const string FiltreMessages = "conversationId = $c and storyId is null and type is not 'story' and +sent_at >= $a and +sent_at < $b";

    /// <summary>Horodatages des messages d'une conversation, dans l'ordre : sert à compter et à découper par année.</summary>
    public List<(long Date, bool Message)> Horodatages(string conversation, long depuis, long avant)
    {
        var liste = new List<(long, bool)>();
        using var c = Commande("select sent_at, type is null or type in ('incoming', 'outgoing') from messages where "
                               + FiltreMessages + " order by sent_at",
            ("$c", conversation), ("$a", depuis), ("$b", avant));
        using var r = c.ExecuteReader();
        while (r.Read()) liste.Add((Long(r, 0), Long(r, 1) == 1));
        return liste;
    }

    /// <summary>Messages d'une conversation, du plus ancien au plus récent, lus au fil de l'eau.</summary>
    public IEnumerable<MessageBrut> Messages(string conversation, long depuis, long avant)
    {
        using var c = Commande(@"select id, type, sent_at, body, sourceServiceId, isErased, isViewOnce, json
                                 from messages where " + FiltreMessages + " order by sent_at, rowid",
            ("$c", conversation), ("$a", depuis), ("$b", avant));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            yield return new MessageBrut
            {
                Id = Chaine(r, 0) ?? "",
                Type = Chaine(r, 1),
                Date = Long(r, 2),
                Corps = Chaine(r, 3),
                SourceAci = Chaine(r, 4),
                Efface = Long(r, 5) == 1,
                VueUnique = Long(r, 6) == 1,
                Json = Chaine(r, 7) ?? "{}",
            };
        }
    }

    /// <summary>Pièces jointes d'une conversation, rangées par message (version actuelle des messages seulement).</summary>
    public Dictionary<string, List<PieceJointe>> PiecesJointes(string conversation)
    {
        var parMessage = new Dictionary<string, List<PieceJointe>>(StringComparer.Ordinal);
        using var c = Commande(@"select messageId, attachmentType, orderInMessage, size, contentType, path, localKey, version,
                                        fileName, caption, flags, width, height,
                                        screenshotPath, screenshotLocalKey, screenshotVersion, screenshotSize
                                 from message_attachments
                                 where conversationId = $c and editHistoryIndex = -1
                                 order by messageId, orderInMessage", ("$c", conversation));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var id = Chaine(r, 0) ?? "";
            if (!parMessage.TryGetValue(id, out var liste)) parMessage[id] = liste = new List<PieceJointe>();
            liste.Add(new PieceJointe
            {
                Genre = Chaine(r, 1) ?? "",
                Ordre = (int)Long(r, 2),
                Taille = Long(r, 3),
                TypeContenu = Chaine(r, 4) ?? "",
                Chemin = Chaine(r, 5),
                CleLocale = Chaine(r, 6),
                Version = EntierOuNul(r, 7),
                NomFichier = Chaine(r, 8),
                Legende = Chaine(r, 9),
                Drapeaux = (int)Long(r, 10),
                Largeur = EntierOuNul(r, 11),
                Hauteur = EntierOuNul(r, 12),
                CheminCapture = Chaine(r, 13),
                CleCapture = Chaine(r, 14),
                VersionCapture = EntierOuNul(r, 15),
                TailleCapture = Long(r, 16),
            });
        }
        return parMessage;
    }

    /// <summary>Détail d'un appel (audio ou vidéo, entrant ou sortant, abouti ou non).</summary>
    public (string Type, string Mode, string Sens, string Etat)? Appel(string? identifiant)
    {
        if (identifiant == null) return null;
        if (_appels == null)
        {
            _appels = new();
            if (TableExiste("callsHistory"))
            {
                using var c = Commande("select callId, type, mode, direction, status from callsHistory");
                using var r = c.ExecuteReader();
                while (r.Read())
                    _appels[Chaine(r, 0) ?? ""] = (Chaine(r, 1) ?? "", Chaine(r, 2) ?? "", Chaine(r, 3) ?? "", Chaine(r, 4) ?? "");
            }
        }
        return _appels.TryGetValue(identifiant, out var a) ? a : null;
    }
}

namespace ExportSignal;

/// <summary>Une conversation Signal (personne ou groupe), avec ses décomptes.</summary>
sealed class Conversation
{
    public string Id = "";
    public string Nom = "";
    public bool Groupe;
    public bool NotesPerso;          // la conversation avec soi-même
    public string? ServiceId;
    public string? Numero;
    public long Messages;            // messages envoyés et reçus
    public long Lignes;              // messages et événements (appels, réglages)
    public long Premier, Dernier;    // horodatages en millisecondes
    public long NombreFichiers;
    public long TailleFichiers;
    public List<string> Membres = new();

    public override string ToString() => Nom;
}

/// <summary>Une ligne de la table messages, telle que lue dans la base.</summary>
sealed class MessageBrut
{
    public string Id = "";
    public string? Type;
    public long Date;
    public string? Corps;
    public string? SourceAci;
    public bool Efface;
    public bool VueUnique;
    public string Json = "{}";
}

/// <summary>Un fichier attaché à un message (photo, vidéo, document, autocollant, image d'aperçu).</summary>
sealed class PieceJointe
{
    public string Genre = "";        // attachment, preview, sticker, quote, long-message, contact
    public int Ordre;
    public long Taille;
    public string TypeContenu = "";
    public string? Chemin;           // chemin relatif dans attachments.noindex
    public string? CleLocale;
    public int? Version;
    public string? NomFichier;
    public string? Legende;
    public int Drapeaux;             // 1 message vocal, 2 sans bordure, 8 GIF
    public int? Largeur, Hauteur;
    public string? CheminCapture;    // image fixe d'une vidéo
    public string? CleCapture;
    public int? VersionCapture;
    public long TailleCapture;

    // Renseignés pendant l'export.
    public string? Exporte;          // chemin relatif dans le dossier de la conversation
    public string? CaptureExportee;
    public bool Absent;              // fichier jamais téléchargé ou disparu du disque

    public bool Vocal => (Drapeaux & 1) != 0;
    public bool Gif => (Drapeaux & 8) != 0;
    public bool Image => TypeContenu.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool Video => TypeContenu.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    public bool Audio => TypeContenu.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
}

[Flags]
enum Style { Aucun = 0, Gras = 1, Italique = 2, Secret = 4, Barre = 8, Chasse = 16 }

/// <summary>Un morceau de texte d'un message, avec sa mise en forme.</summary>
sealed record Segment(string Texte, Style Styles, bool Mention);

sealed class Citation
{
    public string Auteur = "";
    public string Texte = "";
    public long Date;                // horodatage du message cité
}

sealed class Apercu
{
    public string Url = "";
    public string Titre = "";
    public string Description = "";
    public PieceJointe? Image;
}

sealed class ContactPartage
{
    public string Nom = "";
    public List<string> Numeros = new();
}

sealed record Reaction(string Emoji, string Auteur, long Date);

/// <summary>Un message prêt à être écrit, indépendant du format de sortie.</summary>
sealed class Message
{
    public string Id = "";
    public long Date;
    public string Type = "";
    public bool Sortant;
    public string Auteur = "";
    public string? AuteurId;
    public string? Evenement;        // texte d'un événement (appel, réglage) ; null pour un vrai message
    public string? Note;             // mention à la place du contenu (supprimé, vue unique)
    public List<Segment> Segments = new();
    public bool Modifie;
    public Citation? Citation;
    public List<PieceJointe> Fichiers = new();
    public PieceJointe? Autocollant;
    public string? EmojiAutocollant;
    public List<Apercu> Apercus = new();
    public List<ContactPartage> Contacts = new();
    public List<Reaction> Reactions = new();

    public string Texte => string.Concat(Segments.Select(s => s.Texte));
}

/// <summary>Ce que l'utilisateur a demandé.</summary>
sealed class Options
{
    public string Destination = "";
    public List<Conversation> Conversations = new();
    public bool Html = true;
    public bool Texte;
    public bool Json;
    public bool Fichiers = true;
    public DateTime? Depuis;         // jour inclus, heure locale
    public DateTime? Jusqu;          // jour inclus, heure locale
}

sealed record Avancement(double Fraction, string Texte);

/// <summary>Le résultat d'un export.</summary>
sealed class Bilan
{
    public int Conversations;
    public long Messages;
    public long Fichiers;
    public long FichiersAbsents;
    public long Erreurs;
    public string Index = "";
    public string Journal = "";
    public bool Annule;
}

/// <summary>Erreur expliquée en clair à l'utilisateur.</summary>
sealed class ErreurSignal : Exception
{
    public ErreurSignal(string message, Exception? cause = null) : base(message, cause) { }
}

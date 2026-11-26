using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ExportSignal;

/// <summary>Écrit une conversation en texte brut, lisible dans le Bloc-notes.</summary>
sealed class SortieTexte : IDisposable
{
    public const string Fichier = "conversation.txt";
    readonly StreamWriter _w;
    DateTime _jour;

    public SortieTexte(string dossier, Resume r)
    {
        // Avec indicateur d'encodage, pour que les accents s'affichent partout sous Windows.
        _w = new StreamWriter(Path.Combine(dossier, Fichier), false, new UTF8Encoding(true), 1 << 16);
        _w.WriteLine(r.Conv.Nom);
        _w.WriteLine("Conversation Signal exportée le " + Outils.JourCourt(r.ExporteLe) + " à " + Outils.Heure(r.ExporteLe));
        _w.WriteLine(Outils.Pluriel(r.Messages, "message", "messages") + ", du " + Outils.JourCourt(r.Premier) + " au " + Outils.JourCourt(r.Dernier));
        if (r.Conv.Groupe && r.Conv.Membres.Count > 0) _w.WriteLine("Membres : " + string.Join(", ", r.Conv.Membres));
    }

    public void Ecrire(Message m)
    {
        var d = Outils.Local(m.Date);
        if (d.Date != _jour)
        {
            _jour = d.Date;
            var titre = Outils.JourLong(d);
            _w.WriteLine();
            _w.WriteLine(titre);
            _w.WriteLine(new string('-', titre.Length));
        }
        var debut = "[" + Outils.Heure(d) + "] ";
        const string marge = "        ";
        if (m.Evenement != null)
        {
            _w.WriteLine(debut + "* " + m.Evenement);
            return;
        }
        var texte = m.Note ?? m.Texte;
        _w.WriteLine(debut + m.Auteur + " : " + texte.Replace("\r", "").Replace("\n", "\n" + marge) + (m.Modifie ? " (modifié)" : ""));
        if (m.Citation is { } c)
            _w.WriteLine(marge + "en réponse à " + c.Auteur + " : « " + Abrege(c.Texte) + " »");
        foreach (var p in m.Fichiers)
            _w.WriteLine(marge + "pièce jointe : " + (p.Exporte ?? SortieHtml.Libelle(p) + (p.Absent ? ", non disponible sur cet ordinateur" : ", non exportée")));
        if (m.Autocollant != null || m.EmojiAutocollant != null)
            _w.WriteLine(marge + "autocollant " + m.EmojiAutocollant + (m.Autocollant?.Exporte != null ? " : " + m.Autocollant.Exporte : ""));
        foreach (var a in m.Apercus.Where(a => a.Titre.Length > 0))
            _w.WriteLine(marge + "lien : " + a.Titre + " (" + a.Url + ")");
        foreach (var contact in m.Contacts)
            _w.WriteLine(marge + "contact partagé : " + contact.Nom + (contact.Numeros.Count > 0 ? ", " + string.Join(", ", contact.Numeros) : ""));
        if (m.Reactions.Count > 0)
            _w.WriteLine(marge + "réactions : " + string.Join(", ", m.Reactions.Select(r => r.Emoji + " " + r.Auteur)));
    }

    static string Abrege(string texte)
    {
        var ligne = texte.Replace("\r", "").Replace('\n', ' ');
        return ligne.Length > 120 ? ligne[..120] + "..." : ligne;
    }

    public void Dispose() => _w.Dispose();
}

/// <summary>Écrit une conversation en JSON, pour un traitement par un autre programme.</summary>
sealed class SortieJson : IDisposable
{
    public const string Fichier = "conversation.json";
    readonly FileStream _flux;
    readonly Utf8JsonWriter _w;

    public SortieJson(string dossier, Resume r)
    {
        _flux = new FileStream(Path.Combine(dossier, Fichier), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        // Accents et emoji écrits tels quels plutôt qu'en séquences d'échappement.
        _w = new Utf8JsonWriter(_flux, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _w.WriteStartObject();
        _w.WriteString("exporteLe", r.ExporteLe.ToString("yyyy-MM-ddTHH:mm:ss"));
        _w.WriteStartObject("conversation");
        _w.WriteString("nom", r.Conv.Nom);
        _w.WriteString("type", r.Conv.Groupe ? "groupe" : "personne");
        if (r.Conv.Numero != null) _w.WriteString("numero", r.Conv.Numero);
        if (r.Conv.Groupe)
        {
            _w.WriteStartArray("membres");
            foreach (var membre in r.Conv.Membres) _w.WriteStringValue(membre);
            _w.WriteEndArray();
        }
        _w.WriteNumber("messages", r.Messages);
        _w.WriteEndObject();
        _w.WriteStartArray("messages");
    }

    public void Ecrire(Message m)
    {
        _w.WriteStartObject();
        _w.WriteString("date", Outils.Local(m.Date).ToString("yyyy-MM-ddTHH:mm:ss"));
        _w.WriteNumber("horodatage", m.Date);
        if (m.Evenement != null)
        {
            _w.WriteString("genre", "evenement");
            _w.WriteString("type", m.Type);
            _w.WriteString("texte", m.Evenement);
            _w.WriteEndObject();
            return;
        }
        _w.WriteString("genre", "message");
        _w.WriteString("sens", m.Sortant ? "envoye" : "recu");
        _w.WriteString("auteur", m.Auteur);
        _w.WriteString("texte", m.Texte);
        if (m.Note != null) _w.WriteString("note", m.Note);
        if (m.Modifie) _w.WriteBoolean("modifie", true);
        if (m.Citation is { } c)
        {
            _w.WriteStartObject("citation");
            _w.WriteString("auteur", c.Auteur);
            _w.WriteString("texte", c.Texte);
            _w.WriteNumber("horodatage", c.Date);
            _w.WriteEndObject();
        }
        if (m.Fichiers.Count > 0)
        {
            _w.WriteStartArray("piecesJointes");
            foreach (var p in m.Fichiers)
            {
                _w.WriteStartObject();
                if (p.Exporte != null) _w.WriteString("fichier", p.Exporte);
                if (p.NomFichier != null) _w.WriteString("nom", p.NomFichier);
                _w.WriteString("type", p.TypeContenu);
                _w.WriteNumber("taille", p.Taille);
                if (p.Vocal) _w.WriteBoolean("messageVocal", true);
                if (p.Legende != null) _w.WriteString("legende", p.Legende);
                if (p.Exporte == null) _w.WriteBoolean("disponible", !p.Absent);
                _w.WriteEndObject();
            }
            _w.WriteEndArray();
        }
        if (m.Autocollant != null || m.EmojiAutocollant != null)
        {
            _w.WriteStartObject("autocollant");
            if (m.EmojiAutocollant != null) _w.WriteString("emoji", m.EmojiAutocollant);
            if (m.Autocollant?.Exporte != null) _w.WriteString("fichier", m.Autocollant.Exporte);
            _w.WriteEndObject();
        }
        if (m.Apercus.Count > 0)
        {
            _w.WriteStartArray("liens");
            foreach (var a in m.Apercus)
            {
                _w.WriteStartObject();
                _w.WriteString("url", a.Url);
                _w.WriteString("titre", a.Titre);
                if (a.Description.Length > 0) _w.WriteString("description", a.Description);
                _w.WriteEndObject();
            }
            _w.WriteEndArray();
        }
        if (m.Contacts.Count > 0)
        {
            _w.WriteStartArray("contacts");
            foreach (var contact in m.Contacts)
            {
                _w.WriteStartObject();
                _w.WriteString("nom", contact.Nom);
                _w.WriteStartArray("coordonnees");
                foreach (var n in contact.Numeros) _w.WriteStringValue(n);
                _w.WriteEndArray();
                _w.WriteEndObject();
            }
            _w.WriteEndArray();
        }
        if (m.Reactions.Count > 0)
        {
            _w.WriteStartArray("reactions");
            foreach (var r in m.Reactions)
            {
                _w.WriteStartObject();
                _w.WriteString("emoji", r.Emoji);
                _w.WriteString("auteur", r.Auteur);
                if (r.Date > 0) _w.WriteString("date", Outils.Local(r.Date).ToString("yyyy-MM-ddTHH:mm:ss"));
                _w.WriteEndObject();
            }
            _w.WriteEndArray();
        }
        _w.WriteEndObject();
        if (_w.BytesPending > 1 << 16) _w.Flush();
    }

    public void Dispose()
    {
        _w.WriteEndArray();
        _w.WriteEndObject();
        _w.Dispose();
        _flux.Dispose();
    }
}

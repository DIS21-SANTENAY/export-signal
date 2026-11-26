using System.Text;
using System.Text.Json;

namespace ExportSignal;

/// <summary>
/// Transforme une ligne brute de la base en message lisible :
/// auteur, texte mis en forme, citation, fichiers, réactions, ou texte d'un événement (appel, réglage).
/// </summary>
sealed class Interprete
{
    readonly BaseSignal _base;

    public Interprete(BaseSignal source) => _base = source;

    public Message Lire(MessageBrut brut, Conversation conv, Dictionary<string, List<PieceJointe>> pieces)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(brut.Json); }
        catch (JsonException) { doc = JsonDocument.Parse("{}"); }
        using (doc)
        {
            var j = doc.RootElement;
            var type = brut.Type ?? "incoming";
            var m = new Message { Id = brut.Id, Date = brut.Date, Type = type };

            if (type is not ("incoming" or "outgoing"))
            {
                m.Evenement = Evenement(type, j, brut, conv);
                return m;
            }

            m.Sortant = type == "outgoing";
            m.AuteurId = m.Sortant ? _base.MonAci : brut.SourceAci;
            m.Auteur = m.Sortant ? "Moi" : _base.NomDe(brut.SourceAci, conv.Groupe ? "Contact inconnu" : conv.Nom);

            if (j.Vrai("deletedForEveryone"))
            {
                m.Note = "Ce message a été supprimé.";
                return m;
            }
            if (brut.VueUnique)
            {
                m.Note = "Média à vue unique, non conservé par Signal.";
                return m;
            }

            pieces.TryGetValue(brut.Id, out var liste);
            liste ??= new List<PieceJointe>();
            m.Fichiers = liste.Where(p => p.Genre == "attachment").OrderBy(p => p.Ordre).ToList();
            m.Autocollant = liste.FirstOrDefault(p => p.Genre == "sticker");
            m.EmojiAutocollant = j.Objet("sticker")?.Txt("emoji");
            if (j.Objet("sticker") == null) m.Autocollant = null;

            // Texte : Signal coupe les très longs messages et range le texte entier dans un fichier à part.
            var texte = brut.Corps ?? j.Txt("body") ?? "";
            var suite = liste.FirstOrDefault(p => p.Genre == "long-message");
            if (suite != null) texte = TexteEntier(suite) ?? texte;
            m.Segments = Segmenter(texte, j);
            m.Modifie = j.Entier("editMessageTimestamp") != null || j.Liste("editHistory").Any();

            if (j.Objet("quote") is { } q)
            {
                var description = string.Join(", ", q.Liste("attachments").Select(DecrireCite));
                m.Citation = new Citation
                {
                    Auteur = _base.NomDe(q.Txt("authorAci") ?? q.Txt("authorUuid")),
                    Texte = q.Txt("text") is { Length: > 0 } t ? t : (description.Length > 0 ? description : "Message"),
                    Date = q.Entier("id") ?? 0,
                };
            }

            var imageApercu = liste.FirstOrDefault(p => p.Genre == "preview");
            foreach (var a in j.Liste("preview"))
            {
                var url = a.Txt("url");
                if (string.IsNullOrEmpty(url)) continue;
                m.Apercus.Add(new Apercu
                {
                    Url = url,
                    Titre = a.Txt("title") ?? "",
                    Description = a.Txt("description") ?? "",
                    Image = m.Apercus.Count == 0 ? imageApercu : null,
                });
            }

            foreach (var c in j.Liste("contact"))
            {
                var nom = c.Objet("name");
                var contact = new ContactPartage
                {
                    Nom = nom?.Txt("displayName")
                          ?? (((nom?.Txt("givenName") ?? "") + " " + (nom?.Txt("familyName") ?? "")).Trim() is { Length: > 0 } n ? n : null)
                          ?? c.Txt("organization") ?? "Contact",
                };
                contact.Numeros.AddRange(c.Liste("number").Select(x => x.Txt("value")).OfType<string>());
                contact.Numeros.AddRange(c.Liste("email").Select(x => x.Txt("value")).OfType<string>());
                m.Contacts.Add(contact);
            }

            foreach (var r in j.Liste("reactions"))
            {
                var emoji = r.Txt("emoji");
                if (string.IsNullOrEmpty(emoji)) continue;   // réaction retirée
                m.Reactions.Add(new Reaction(emoji, _base.NomDe(r.Txt("fromId")), r.Entier("timestamp") ?? 0));
            }

            if (j.Objet("poll") is { } sondage) AjouterSondage(m, sondage);

            bool vide = m.Segments.Count == 0 && m.Fichiers.Count == 0 && m.Autocollant == null
                        && m.EmojiAutocollant == null && m.Apercus.Count == 0 && m.Contacts.Count == 0;
            if (vide) m.Note = brut.Efface ? "Message supprimé." : "Message sans contenu affichable.";
            return m;
        }
    }

    string? TexteEntier(PieceJointe p)
    {
        var source = _base.CheminSource(p.Chemin);
        if (source == null) return null;
        try
        {
            return Encoding.UTF8.GetString(Dechiffrement.EnMemoire(source, p.CleLocale, p.Version, p.Taille));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException
                                      or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            return null;   // on garde alors le début du texte, présent dans la base
        }
    }

    static string DecrireCite(JsonElement piece)
    {
        var type = piece.Txt("contentType") ?? "";
        if (type.StartsWith("image/")) return "Photo";
        if (type.StartsWith("video/")) return "Vidéo";
        if (type.StartsWith("audio/")) return "Message audio";
        return piece.Txt("fileName") ?? "Fichier";
    }

    static void AjouterSondage(Message m, JsonElement sondage)
    {
        var sb = new StringBuilder("Sondage : " + (sondage.Txt("question") ?? ""));
        foreach (var option in sondage.Liste("options"))
        {
            var libelle = option.ValueKind == JsonValueKind.String ? option.GetString() : option.Txt("text");
            if (!string.IsNullOrEmpty(libelle)) sb.Append("\n  ").Append(libelle);
        }
        m.Segments.Add(new Segment(sb.ToString(), Style.Aucun, false));
    }

    /// <summary>
    /// Découpe le texte selon les plages de mise en forme de Signal (gras, italique, texte masqué, barré, chasse fixe)
    /// et remplace les mentions par le nom de la personne. Les positions sont en unités UTF-16, comme les chaînes .NET.
    /// </summary>
    List<Segment> Segmenter(string texte, JsonElement message)
    {
        var segments = new List<Segment>();
        if (texte.Length == 0) return segments;

        var plages = new List<(int Debut, int Fin, Style Style, string? Mention)>();
        foreach (var r in message.Liste("bodyRanges"))
        {
            int debut = (int)Math.Clamp(r.Entier("start") ?? -1, -1, texte.Length);
            int fin = (int)Math.Clamp(debut + (r.Entier("length") ?? 0), 0, texte.Length);
            if (debut < 0 || fin <= debut) continue;
            var aci = r.Txt("mentionAci") ?? r.Txt("mentionUuid");
            if (aci != null) { plages.Add((debut, fin, Style.Aucun, "@" + _base.NomDe(aci))); continue; }
            var style = r.Entier("style") switch
            {
                1 => Style.Gras, 2 => Style.Italique, 3 => Style.Secret, 4 => Style.Barre, 5 => Style.Chasse, _ => Style.Aucun,
            };
            if (style != Style.Aucun) plages.Add((debut, fin, style, null));
        }
        if (plages.Count == 0)
        {
            segments.Add(new Segment(texte, Style.Aucun, false));
            return segments;
        }

        var coupes = new SortedSet<int> { 0, texte.Length };
        foreach (var p in plages) { coupes.Add(p.Debut); coupes.Add(p.Fin); }
        int precedente = -1;
        foreach (int coupe in coupes)
        {
            if (precedente >= 0 && coupe > precedente)
            {
                int a = precedente, b = coupe;
                var style = Style.Aucun;
                string? mention = null;
                bool debutMention = false;
                foreach (var p in plages)
                {
                    if (p.Debut > a || p.Fin < b) continue;   // la plage ne couvre pas ce morceau
                    style |= p.Style;
                    if (p.Mention != null && mention == null) { mention = p.Mention; debutMention = p.Debut == a; }
                }
                if (mention == null) segments.Add(new Segment(texte[a..b], style, false));
                else if (debutMention) segments.Add(new Segment(mention, style, true));
            }
            precedente = coupe;
        }
        return segments;
    }

    string Evenement(string type, JsonElement j, MessageBrut brut, Conversation conv)
    {
        var qui = _base.NomDe(brut.SourceAci, conv.Nom);
        switch (type)
        {
            case "call-history":
                if (_base.Appel(j.Txt("callId")) is not { } appel) return "Appel";
                if (appel.Mode == "Group" || appel.Type == "Group")
                    return appel.Etat == "Missed" ? "Appel de groupe manqué" : "Appel de groupe";
                var genre = appel.Type == "Video" ? "Appel vidéo" : "Appel audio";
                bool entrant = appel.Sens == "Incoming";
                return appel.Etat switch
                {
                    "Missed" => entrant ? genre + " manqué" : genre + " sortant sans réponse",
                    "Declined" => entrant ? genre + " refusé" : genre + " sortant refusé",
                    _ => genre + (entrant ? " entrant" : " sortant"),
                };
            case "keychange":
                return "Le numéro de sécurité de " + _base.NomDe(j.Txt("key_changed"), conv.Nom) + " a changé.";
            case "verified-change":
                return "Numéro de sécurité de " + _base.NomDe(j.Txt("verifiedChanged"), conv.Nom)
                       + (j.Vrai("verified") ? " marqué comme vérifié." : " marqué comme non vérifié.");
            case "timer-notification":
                var delai = j.Objet("expirationTimerUpdate")?.Entier("expireTimer") ?? 0;
                return delai > 0 ? "Messages éphémères réglés sur " + Outils.Duree(delai) + "." : "Messages éphémères désactivés.";
            case "universal-timer-notification":
                return "Messages éphémères activés par défaut pour cette conversation.";
            case "profile-change":
                var profil = j.Objet("profileChange");
                return (profil?.Txt("oldName") ?? qui) + " a changé de nom de profil et s'appelle maintenant "
                       + (profil?.Txt("newName") ?? "autrement") + ".";
            case "message-request-response-event":
                return j.Txt("messageRequestResponseEvent") switch
                {
                    "ACCEPT" => "Demande de conversation acceptée.",
                    "BLOCK" => "Conversation bloquée.",
                    "UNBLOCK" => "Conversation débloquée.",
                    "SPAM" => "Conversation signalée comme indésirable.",
                    _ => "Réponse à une demande de conversation.",
                };
            case "group-v2-change": return "Le groupe a été modifié.";
            case "group-v1-migration": return "Le groupe a été mis à niveau.";
            case "change-number-notification": return qui + " a changé de numéro de téléphone.";
            case "joined-signal-notification": return conv.Nom + " utilise Signal.";
            case "chat-session-refreshed": return "La session de conversation a été réinitialisée.";
            case "delivery-issue": return "Un message n'a pas pu être remis.";
            case "conversation-merge": return "Deux conversations avec ce contact ont été fusionnées.";
            case "contact-removed-notification": return "Ce contact a été retiré.";
            default: return "Événement Signal (" + type + ")";
        }
    }
}

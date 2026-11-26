using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ExportSignal;

/// <summary>Ce qu'il faut savoir d'une conversation pour écrire ses pages.</summary>
sealed class Resume
{
    public Conversation Conv = new();
    public string Dossier = "";          // nom du dossier de la conversation dans l'export
    public long Messages;                // messages envoyés et reçus dans la période
    public long Lignes;                  // messages et événements
    public DateTime Premier, Dernier;
    public DateTime ExporteLe;
    public List<(int Annee, int Nombre)> Annees = new();
    public bool Decoupe;                 // une page par année
    public HashSet<long> Dates = new();  // horodatages présents dans l'export, pour les liens vers les messages cités
}

/// <summary>
/// Écrit une conversation en pages web autonomes (style et script inclus, aucune connexion nécessaire).
/// Les grosses conversations sont découpées en une page par année, avec une page de sommaire.
/// </summary>
sealed class SortieHtml : IDisposable
{
    public const string Page = "conversation.html";

    readonly string _dossier;
    readonly Resume _r;
    StreamWriter? _w;
    int _annee;
    DateTime _jour;

    public SortieHtml(string dossier, Resume resume)
    {
        _dossier = dossier;
        _r = resume;
    }

    static string NomPage(int annee) => "annee-" + annee + ".html";

    static string E(string? texte) => WebUtility.HtmlEncode(texte ?? "");

    /// <summary>Adresse relative d'un fichier exporté, utilisable dans un lien.</summary>
    static string Lien(string relatif) => string.Join('/', relatif.Split('/').Select(Uri.EscapeDataString));

    static StreamWriter Creer(string chemin) => new(chemin, false, new UTF8Encoding(false), 1 << 16);

    // ------------------------------------------------------------------ pages

    void Ouvrir(int annee)
    {
        Fermer();
        _annee = annee;
        _jour = DateTime.MinValue;
        _w = Creer(Path.Combine(_dossier, _r.Decoupe ? NomPage(annee) : Page));
        var titre = _r.Decoupe ? _r.Conv.Nom + ", " + annee : _r.Conv.Nom;
        EnTete(_w, titre, _r.Conv.Nom, SousTitre(_r.Decoupe ? annee : null), "../index.html", recherche: true);
        if (_r.Decoupe)
        {
            _w.Write("<nav class=\"annees\"><a href=\"" + Page + "\">Sommaire</a>");
            foreach (var (a, _) in _r.Annees)
                _w.Write("<a" + (a == annee ? " class=\"ici\"" : "") + " href=\"" + NomPage(a) + "\">" + a + "</a>");
            _w.WriteLine("</nav>");
        }
        _w.WriteLine("<main>");
        if (!_r.Decoupe) Membres(_w);
    }

    void Fermer()
    {
        if (_w == null) return;
        _w.WriteLine("</main>");
        Pied(_w, _r.ExporteLe, script: true);
        _w.Dispose();
        _w = null;
    }

    public void Dispose()
    {
        Fermer();
        if (!_r.Decoupe) return;
        // Page de sommaire d'une conversation découpée par année.
        using var w = Creer(Path.Combine(_dossier, Page));
        EnTete(w, _r.Conv.Nom, _r.Conv.Nom, SousTitre(null), "../index.html", recherche: false);
        w.WriteLine("<main>");
        Membres(w);
        w.WriteLine("<p class=\"intro\">Cette conversation est longue : elle est présentée année par année.</p>");
        w.WriteLine("<div class=\"cartes\">");
        foreach (var (a, n) in _r.Annees)
            w.WriteLine("<a class=\"carte\" href=\"" + NomPage(a) + "\"><b>" + a + "</b><span>"
                        + E(Outils.Pluriel(n, "message", "messages")) + "</span></a>");
        w.WriteLine("</div></main>");
        Pied(w, _r.ExporteLe, script: false);
    }

    string SousTitre(int? annee)
    {
        if (annee is { } a)
        {
            var n = _r.Annees.FirstOrDefault(x => x.Annee == a).Nombre;
            return Outils.Pluriel(n, "message", "messages") + " en " + a;
        }
        var periode = _r.Premier.Date == _r.Dernier.Date
            ? "le " + _r.Premier.ToString("d MMMM yyyy", Outils.Fr)
            : "du " + _r.Premier.ToString("d MMMM yyyy", Outils.Fr) + " au " + _r.Dernier.ToString("d MMMM yyyy", Outils.Fr);
        return Outils.Pluriel(_r.Messages, "message", "messages") + ", " + periode;
    }

    void Membres(TextWriter w)
    {
        if (_r.Conv.Groupe && _r.Conv.Membres.Count > 0)
            w.WriteLine("<p class=\"intro\">Groupe de " + _r.Conv.Membres.Count + " membres : "
                        + E(string.Join(", ", _r.Conv.Membres)) + ".</p>");
    }

    static void EnTete(TextWriter w, string titrePage, string titre, string sousTitre, string? retour, bool recherche)
    {
        w.WriteLine("<!doctype html>");
        w.WriteLine("<html lang=\"fr\"><head><meta charset=\"utf-8\">");
        w.WriteLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        w.WriteLine("<title>" + E(titrePage) + ", conversation Signal</title>");
        w.WriteLine("<style>" + Css + "</style></head><body>");
        w.Write("<header class=\"entete\"><div class=\"titre\"><h1>" + E(titre) + "</h1><p>" + E(sousTitre) + "</p></div>");
        if (recherche)
            w.Write("<input type=\"search\" id=\"filtre\" placeholder=\"Rechercher dans cette page\" aria-label=\"Rechercher dans cette page\">");
        if (retour != null) w.Write("<a class=\"retour\" href=\"" + retour + "\">Toutes les conversations</a>");
        w.WriteLine("</header>");
    }

    static void Pied(TextWriter w, DateTime exporteLe, bool script)
    {
        w.WriteLine("<footer>Export réalisé le " + exporteLe.ToString("d MMMM yyyy", Outils.Fr) + " à "
                    + Outils.Heure(exporteLe) + " avec Export Signal.</footer>");
        if (script) w.WriteLine("<script>" + Script + "</script>");
        w.WriteLine("</body></html>");
    }

    // ------------------------------------------------------------------ messages

    public void Ecrire(Message m)
    {
        var d = Outils.Local(m.Date);
        if (_w == null || (_r.Decoupe && d.Year != _annee)) Ouvrir(d.Year);
        if (d.Date != _jour)
        {
            _jour = d.Date;
            _w!.WriteLine("<div class=\"jour\"><span>" + E(Outils.JourLong(d)) + "</span></div>");
        }

        var sb = new StringBuilder();
        if (m.Evenement != null)
        {
            sb.Append("<div class=\"evt\" id=\"t").Append(m.Date).Append("\">").Append(E(m.Evenement))
              .Append(" <time>").Append(Outils.Heure(d)).Append("</time></div>");
            _w!.WriteLine(sb);
            return;
        }

        sb.Append("<div class=\"msg ").Append(m.Sortant ? "envoi" : "recu").Append("\" id=\"t").Append(m.Date).Append("\"><div class=\"bulle\">");
        if (_r.Conv.Groupe && !m.Sortant)
            sb.Append("<div class=\"auteur\" style=\"color:").Append(Couleur(m.AuteurId ?? m.Auteur)).Append("\">").Append(E(m.Auteur)).Append("</div>");

        if (m.Citation is { } c)
        {
            // Lien vers le message cité s'il fait partie de l'export ; simple encadré sinon (message effacé ou hors période).
            bool present = _r.Dates.Contains(c.Date);
            sb.Append(present ? "<a class=\"cit\" href=\"" + LienMessage(c.Date) + "\">" : "<div class=\"cit\">")
              .Append("<b>").Append(E(c.Auteur)).Append("</b><span>").Append(E(c.Texte)).Append("</span>")
              .Append(present ? "</a>" : "</div>");
        }

        if (m.Fichiers.Count > 0)
        {
            int visuels = m.Fichiers.Count(p => p.Exporte != null && p.Image);
            sb.Append("<div class=\"pj").Append(visuels > 1 ? " plusieurs" : "").Append("\">");
            foreach (var p in m.Fichiers) Piece(sb, p);
            sb.Append("</div>");
        }

        foreach (var a in m.Apercus)
        {
            sb.Append("<a class=\"apercu\" href=\"").Append(E(a.Url)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            if (a.Image?.Exporte != null)
                sb.Append("<img loading=\"lazy\" alt=\"\" src=\"").Append(Lien(a.Image.Exporte)).Append("\">");
            sb.Append("<b>").Append(E(a.Titre.Length > 0 ? a.Titre : a.Url)).Append("</b>");
            if (a.Description.Length > 0) sb.Append("<span>").Append(E(a.Description)).Append("</span>");
            sb.Append("<small>").Append(E(Domaine(a.Url))).Append("</small></a>");
        }

        foreach (var contact in m.Contacts)
        {
            sb.Append("<div class=\"fichier\"><b>Contact partagé : ").Append(E(contact.Nom)).Append("</b>");
            if (contact.Numeros.Count > 0) sb.Append("<br>").Append(E(string.Join(", ", contact.Numeros)));
            sb.Append("</div>");
        }

        if (m.Autocollant?.Exporte != null)
            sb.Append("<div class=\"autocollant\"><img loading=\"lazy\" alt=\"").Append(E("Autocollant " + m.EmojiAutocollant))
              .Append("\" src=\"").Append(Lien(m.Autocollant.Exporte)).Append("\"></div>");
        else if (m.Autocollant != null || m.EmojiAutocollant != null)
            sb.Append("<div class=\"note\">Autocollant ").Append(E(m.EmojiAutocollant)).Append("</div>");

        if (m.Segments.Count > 0)
        {
            sb.Append("<div class=\"txt\">");
            foreach (var s in m.Segments) EcrireSegment(sb, s);
            sb.Append("</div>");
        }
        if (m.Note != null) sb.Append("<div class=\"note\">").Append(E(m.Note)).Append("</div>");

        sb.Append("<div class=\"pied\">").Append(m.Modifie ? "modifié, " : "").Append("<time>").Append(Outils.Heure(d)).Append("</time></div></div>");

        if (m.Reactions.Count > 0)
        {
            sb.Append("<div class=\"reac\">");
            foreach (var groupe in m.Reactions.GroupBy(r => r.Emoji))
            {
                sb.Append("<span title=\"").Append(E(string.Join(", ", groupe.Select(r => r.Auteur)))).Append("\">").Append(E(groupe.Key));
                if (groupe.Count() > 1) sb.Append(' ').Append(groupe.Count());
                sb.Append("</span>");
            }
            sb.Append("</div>");
        }
        sb.Append("</div>");
        _w!.WriteLine(sb);
    }

    /// <summary>Lien vers un autre message de la conversation (message cité), éventuellement sur la page d'une autre année.</summary>
    string LienMessage(long date)
    {
        if (!_r.Decoupe) return "#t" + date;
        int annee = Outils.Local(date).Year;
        return (annee == _annee ? "" : NomPage(annee)) + "#t" + date;
    }

    static void Piece(StringBuilder sb, PieceJointe p)
    {
        if (p.Exporte == null)
        {
            sb.Append("<div class=\"absent\">").Append(E(Libelle(p))).Append(p.Absent ? ", non disponible sur cet ordinateur" : ", non exporté").Append("</div>");
            return;
        }
        var href = Lien(p.Exporte);
        if (p.Image)
        {
            sb.Append("<a href=\"").Append(href).Append("\" target=\"_blank\"><img loading=\"lazy\" alt=\"").Append(E(p.Legende ?? p.NomFichier ?? "Photo")).Append("\" src=\"").Append(href).Append('"');
            if (p.Largeur > 0 && p.Hauteur > 0) sb.Append(" width=\"").Append(p.Largeur).Append("\" height=\"").Append(p.Hauteur).Append('"');
            sb.Append("></a>");
        }
        else if (p.Video)
        {
            sb.Append("<video controls").Append(p.Gif ? " loop muted" : "");
            if (p.CaptureExportee != null) sb.Append(" preload=\"none\" poster=\"").Append(Lien(p.CaptureExportee)).Append('"');
            else sb.Append(" preload=\"metadata\"");
            sb.Append(" src=\"").Append(href).Append("\"></video>");
        }
        else if (p.Audio)
        {
            sb.Append("<div class=\"son\"><span>").Append(p.Vocal ? "Message vocal" : E(p.NomFichier ?? "Fichier audio"))
              .Append("</span><audio controls preload=\"none\" src=\"").Append(href).Append("\"></audio></div>");
        }
        else
        {
            sb.Append("<a class=\"fichier\" href=\"").Append(href).Append("\" target=\"_blank\">").Append(E(Libelle(p))).Append("</a>");
        }
        if (!string.IsNullOrWhiteSpace(p.Legende)) sb.Append("<div class=\"legende\">").Append(E(p.Legende)).Append("</div>");
    }

    public static string Libelle(PieceJointe p)
    {
        var nom = p.NomFichier ?? (p.Image ? "Photo" : p.Video ? "Vidéo" : p.Vocal ? "Message vocal" : p.Audio ? "Fichier audio" : "Fichier");
        return nom + " (" + Outils.Taille(p.Taille) + ")";
    }

    static string Domaine(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // Couleurs des noms dans les groupes : bleus, verts, violets, lisibles sur fond clair comme sur fond sombre.
    static readonly string[] Palette = { "#2f7fd1", "#1f9d8a", "#8a63d2", "#c2478f", "#3b9a4b", "#4f6fd8", "#0f8fb3", "#a0529e" };

    static string Couleur(string cle)
    {
        uint h = 2166136261;
        foreach (var c in cle) h = (h ^ c) * 16777619;
        return Palette[h % (uint)Palette.Length];
    }

    static readonly Regex Adresse = new(@"https?://[^\s<>""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static void EcrireSegment(StringBuilder sb, Segment s)
    {
        string fin = "";
        if (s.Styles.HasFlag(Style.Gras)) { sb.Append("<b>"); fin = "</b>" + fin; }
        if (s.Styles.HasFlag(Style.Italique)) { sb.Append("<i>"); fin = "</i>" + fin; }
        if (s.Styles.HasFlag(Style.Barre)) { sb.Append("<s>"); fin = "</s>" + fin; }
        if (s.Styles.HasFlag(Style.Chasse)) { sb.Append("<code>"); fin = "</code>" + fin; }
        if (s.Styles.HasFlag(Style.Secret)) { sb.Append("<span class=\"secret\">"); fin = "</span>" + fin; }

        if (s.Mention)
        {
            sb.Append("<span class=\"mention\">").Append(E(s.Texte)).Append("</span>");
        }
        else
        {
            // Les adresses web deviennent des liens cliquables.
            int position = 0;
            foreach (Match lien in Adresse.Matches(s.Texte))
            {
                var url = lien.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
                sb.Append(E(s.Texte[position..lien.Index]));
                sb.Append("<a href=\"").Append(E(url)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">").Append(E(url)).Append("</a>");
                position = lien.Index + url.Length;
            }
            sb.Append(E(s.Texte[position..]));
        }
        sb.Append(fin);
    }

    // ------------------------------------------------------------------ index général

    /// <summary>Écrit la page d'accueil de l'export : la liste des conversations exportées.</summary>
    public static void EcrireIndex(string chemin, List<Resume> conversations, DateTime exporteLe, bool pages)
    {
        using var w = Creer(chemin);
        long total = conversations.Sum(c => c.Messages);
        EnTete(w, "Export Signal", "Export Signal",
            Outils.Pluriel(conversations.Count, "conversation", "conversations") + ", " + Outils.Pluriel(total, "message", "messages"),
            null, recherche: true);
        w.WriteLine("<main><table class=\"liste\"><thead><tr><th>Conversation</th><th>Type</th><th class=\"n\">Messages</th><th>Premier message</th><th>Dernier message</th></tr></thead><tbody>");
        foreach (var c in conversations.OrderByDescending(c => c.Dernier))
        {
            var nom = E(c.Conv.Nom);
            if (pages) nom = "<a href=\"" + Lien(c.Dossier + "/" + Page) + "\">" + nom + "</a>";
            w.WriteLine("<tr class=\"msg\"><td>" + nom + "</td><td>" + (c.Conv.Groupe ? "Groupe" : "Personne") + "</td><td class=\"n\">"
                        + Outils.Nombre(c.Messages) + "</td><td>" + Outils.JourCourt(c.Premier) + "</td><td>" + Outils.JourCourt(c.Dernier) + "</td></tr>");
        }
        w.WriteLine("</tbody></table></main>");
        Pied(w, exporteLe, script: true);
    }

    // ------------------------------------------------------------------ style et script

    const string Css = """
:root{--fond:#fff;--texte:#1c2733;--doux:#5b6b7c;--recu:#eaf1f8;--envoi:#3271b7;--marine:#18365e;--ciel:#90cdf4;--trait:#d5dfea;--carte:#f5f8fb;--nom:#18365e}
@media (prefers-color-scheme:dark){:root{--fond:#0f151c;--texte:#e4ebf3;--doux:#9aa9b9;--recu:#1d2936;--envoi:#2b639f;--trait:#2a3948;--carte:#18222d;--nom:#90cdf4}}
*{box-sizing:border-box}
html{scroll-padding-top:96px}
body{margin:0;background:var(--fond);color:var(--texte);font:15px/1.45 Lato,"Segoe UI",system-ui,sans-serif}
[hidden]{display:none!important}
a{color:inherit}
.entete{position:sticky;top:0;z-index:5;background:var(--marine);color:#fff;padding:12px 28px;display:flex;gap:18px;align-items:center;flex-wrap:wrap}
.entete .titre{flex:1 1 320px;min-width:0}
.entete h1{margin:0;font:600 20px/1.25 Montserrat,"Segoe UI",system-ui,sans-serif}
.entete p{margin:2px 0 0;font-size:13px;color:#c9dcf0}
.entete input{flex:0 1 300px;min-width:180px;padding:8px 12px;border:0;border-radius:6px;font:inherit;background:#fff;color:#1c2733}
.entete .retour{font-size:13.5px;color:#c9dcf0}
.annees{display:flex;flex-wrap:wrap;gap:6px;padding:10px 28px;border-bottom:1px solid var(--trait)}
.annees a{padding:3px 12px;border-radius:14px;border:1px solid var(--trait);text-decoration:none;font-size:14px}
.annees a.ici{background:var(--envoi);border-color:var(--envoi);color:#fff}
main{padding:10px 28px 40px;max-width:1240px;margin:0 auto}
.intro{color:var(--doux);font-size:14px;margin:12px 0}
.jour{text-align:center;margin:22px 0 10px;color:var(--doux);font-size:13px}
.jour span{background:var(--carte);border:1px solid var(--trait);border-radius:12px;padding:3px 12px}
.evt{text-align:center;color:var(--doux);font-size:13px;margin:8px 0}
.evt time{opacity:.8;margin-left:4px}
.msg{display:flex;flex-direction:column;align-items:flex-start;margin:3px 0}
.msg.envoi{align-items:flex-end}
.bulle{max-width:min(700px,84%);background:var(--recu);border-radius:14px;padding:8px 12px;overflow-wrap:anywhere}
.envoi .bulle{background:var(--envoi);color:#fff}
.msg:target .bulle{outline:3px solid var(--ciel);outline-offset:2px}
.auteur{font-weight:700;font-size:13px;margin-bottom:2px}
.txt{white-space:pre-wrap}
.txt code{font-family:Consolas,"Cascadia Mono",monospace;font-size:14px}
.secret{background:rgba(128,128,128,.28);border-radius:3px}
.mention{font-weight:700}
.note{font-style:italic;opacity:.85}
.pied{font-size:11.5px;opacity:.75;text-align:right;margin-top:2px}
.cit{display:block;margin:0 0 6px;padding:5px 9px;border-left:3px solid var(--ciel);background:rgba(128,128,128,.14);border-radius:6px;font-size:13.5px;text-decoration:none}
.envoi .cit{background:rgba(255,255,255,.16)}
.cit b{display:block;font-size:12.5px}
.cit span{display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;white-space:pre-wrap}
.pj{display:flex;flex-wrap:wrap;align-items:flex-start;gap:4px;margin:2px 0 6px}
.pj .fichier,.pj .absent,.pj .son{flex:1 1 100%}
.pj img,.pj video{display:block;max-width:100%;max-height:440px;width:auto;height:auto;border-radius:8px;background:rgba(128,128,128,.15)}
.pj.plusieurs img{width:190px;height:190px;object-fit:cover}
.son span{display:block;font-size:13px;margin-bottom:3px}
.son audio{width:320px;max-width:100%;display:block}
.fichier,.absent,.apercu{display:block;padding:8px 10px;border-radius:8px;background:rgba(128,128,128,.14);font-size:13.5px}
.envoi .fichier,.envoi .absent,.envoi .apercu{background:rgba(255,255,255,.16)}
.absent{font-style:italic;opacity:.9}
.legende{flex-basis:100%;font-size:13.5px}
.apercu{text-decoration:none;margin:0 0 6px}
.apercu img{display:block;max-width:100%;max-height:220px;border-radius:6px;margin-bottom:6px}
.apercu b,.apercu span,.apercu small{display:block}
.apercu small{opacity:.8}
.autocollant img{display:block;width:150px;height:150px;object-fit:contain}
.reac{display:flex;gap:4px;flex-wrap:wrap;margin:-3px 8px 4px}
.reac span{background:var(--carte);border:1px solid var(--trait);border-radius:11px;padding:0 7px;font-size:13px;color:var(--texte)}
.cartes{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px;margin-top:16px}
.carte{display:block;padding:16px;border:1px solid var(--trait);border-radius:10px;background:var(--carte);text-decoration:none}
.carte b{display:block;font:600 22px Montserrat,"Segoe UI",sans-serif;color:var(--nom)}
.carte span{color:var(--doux);font-size:14px}
.liste{width:100%;border-collapse:collapse;margin-top:14px}
.liste th,.liste td{text-align:left;padding:9px 12px;border-bottom:1px solid var(--trait)}
.liste th{font-size:13px;color:var(--doux);font-weight:600}
.liste .n{text-align:right}
.liste a{color:var(--nom);font-weight:700;text-decoration:none}
.liste a:hover{text-decoration:underline}
.liste tr.msg{display:table-row}
footer{padding:16px 28px 28px;color:var(--doux);font-size:12.5px;text-align:center}
@media print{.entete{position:static;background:none;color:#000;padding:0 0 8px}.entete p{color:#333}.entete input,.entete .retour,.annees{display:none}main{padding:0;max-width:none}.bulle{border:1px solid #999;break-inside:avoid}.envoi .bulle{background:#e6eef7;color:#000}video,audio{display:none}}
""";

    // Recherche dans la page : masque les messages qui ne contiennent pas le texte saisi.
    const string Script = """
(function(){var f=document.getElementById('filtre');if(!f)return;var t;
f.addEventListener('input',function(){clearTimeout(t);t=setTimeout(function(){
var q=f.value.trim().toLowerCase();
document.querySelectorAll('.msg,.evt').forEach(function(e){e.hidden=q!==''&&e.textContent.toLowerCase().indexOf(q)<0;});
document.querySelectorAll('.jour').forEach(function(j){var n=j.nextElementSibling,v=false;
while(n&&!n.classList.contains('jour')){if(!n.hidden){v=true;break;}n=n.nextElementSibling;}j.hidden=!v;});
},200);});})();
""";
}

using System.Drawing.Imaging;

namespace ExportSignal;

/// <summary>
/// Jeu d'essai entièrement fictif : sert à contrôler l'aspect des pages et de la fenêtre
/// sans afficher de vraies conversations (ExportSignal.exe --demo DOSSIER).
/// </summary>
static class Demonstration
{
    public static List<Conversation> Conversations()
    {
        long jour = 86_400_000, maintenant = Outils.VersMs(new DateTime(2026, 11, 20, 18, 30, 0));
        return new List<Conversation>
        {
            new() { Id = "1", Nom = "Famille Martin", Groupe = true, Messages = 12840, Lignes = 12900, TailleFichiers = 1_650_000_000, Premier = maintenant - 900 * jour, Dernier = maintenant, Membres = { "Camille Martin", "Louis Martin", "Moi" } },
            new() { Id = "2", Nom = "Camille Martin", Messages = 5321, Lignes = 5400, TailleFichiers = 420_000_000, Premier = maintenant - 1100 * jour, Dernier = maintenant - jour },
            new() { Id = "3", Nom = "Atelier du bois", Groupe = true, Messages = 842, Lignes = 850, TailleFichiers = 96_000_000, Premier = maintenant - 400 * jour, Dernier = maintenant - 3 * jour },
            new() { Id = "4", Nom = "Louis Martin", Messages = 310, Lignes = 322, TailleFichiers = 8_400_000, Premier = maintenant - 700 * jour, Dernier = maintenant - 12 * jour },
            new() { Id = "5", Nom = "Notes à moi-même", Messages = 57, Lignes = 57, TailleFichiers = 0, Premier = maintenant - 300 * jour, Dernier = maintenant - 40 * jour },
        };
    }

    /// <summary>Écrit un petit export fictif qui passe par tous les cas d'affichage.</summary>
    public static string Ecrire(string destination)
    {
        var conv = Conversations()[0];
        var maintenant = DateTime.Now;
        var debut = new DateTime(2026, 11, 19, 9, 12, 0);
        long T(double minutes) => Outils.VersMs(debut.AddMinutes(minutes));

        var dossier = Path.Combine(destination, "Famille Martin");
        Directory.CreateDirectory(Path.Combine(dossier, "fichiers"));
        Image(Path.Combine(dossier, "fichiers", "photo1.png"), 640, 420, Color.FromArgb(0x90, 0xcd, 0xf4), "Photo 1");
        Image(Path.Combine(dossier, "fichiers", "photo2.png"), 420, 420, Color.FromArgb(0x7f, 0xc8, 0xa9), "Photo 2");
        Image(Path.Combine(dossier, "fichiers", "photo3.png"), 420, 420, Color.FromArgb(0xb9, 0xa6, 0xe6), "Photo 3");
        File.WriteAllText(Path.Combine(dossier, "fichiers", "devis.txt"), "Document fictif.");

        PieceJointe Photo(string nom, int l, int h) => new() { Genre = "attachment", TypeContenu = "image/png", Taille = 48_000, Largeur = l, Hauteur = h, Exporte = "fichiers/" + nom };

        var messages = new List<Message>
        {
            new() { Date = T(0), Type = "incoming", Auteur = "Camille Martin", AuteurId = "camille", Segments = { new Segment("Bonjour à tous, qui vient dimanche ?", Style.Aucun, false) },
                    Reactions = { new Reaction("👍", "Moi", T(1)), new Reaction("👍", "Louis Martin", T(2)), new Reaction("❤️", "Louis Martin", T(2)) } },
            new() { Date = T(3), Type = "outgoing", Sortant = true, Auteur = "Moi", Segments = { new Segment("Présent. J'apporte le ", Style.Aucun, false), new Segment("dessert", Style.Gras, false), new Segment(" et le ", Style.Aucun, false), new Segment("café", Style.Italique, false), new Segment(". Recette ici : https://www.exemple.fr/recettes/tarte-aux-pommes.", Style.Aucun, false) },
                    Modifie = true, Citation = new Citation { Auteur = "Camille Martin", Texte = "Bonjour à tous, qui vient dimanche ?", Date = T(0) } },
            new() { Date = T(5), Type = "incoming", Auteur = "Louis Martin", AuteurId = "louis", Segments = { new Segment("Voici le plan de table.", Style.Aucun, false) }, Fichiers = { Photo("photo1.png", 640, 420) } },
            new() { Date = T(6), Type = "call-history", Evenement = "Appel vidéo entrant" },
            new() { Date = T(20), Type = "outgoing", Sortant = true, Auteur = "Moi", Fichiers = { Photo("photo2.png", 420, 420), Photo("photo3.png", 420, 420),
                        new PieceJointe { Genre = "attachment", TypeContenu = "application/pdf", NomFichier = "devis.txt", Taille = 280_000, Exporte = "fichiers/devis.txt" },
                        new PieceJointe { Genre = "attachment", TypeContenu = "image/jpeg", Taille = 1_250_000, Absent = true } } },
            new() { Date = T(24), Type = "incoming", Auteur = "Camille Martin", AuteurId = "camille", Segments = { new Segment("@Louis Martin", Style.Aucun, true), new Segment(" tu peux prendre ", Style.Aucun, false), new Segment("le pain", Style.Barre, false), new Segment(" les fleurs ?", Style.Aucun, false) },
                    Apercus = { new Apercu { Url = "https://www.exemple.fr/fleuriste", Titre = "Fleuriste du centre", Description = "Bouquets de saison, ouvert le dimanche matin." } } },
            new() { Date = T(26), Type = "incoming", Auteur = "Louis Martin", AuteurId = "louis", Note = "Ce message a été supprimé." },
            new() { Date = T(27), Type = "incoming", Auteur = "Louis Martin", AuteurId = "louis", EmojiAutocollant = "🎉",
                    Contacts = { new ContactPartage { Nom = "Fleuriste du centre", Numeros = { "03 80 00 00 00" } } } },
            new() { Date = T(60 * 26), Type = "timer-notification", Evenement = "Messages éphémères désactivés." },
            new() { Date = T(60 * 26 + 4), Type = "outgoing", Sortant = true, Auteur = "Moi", Segments = { new Segment("Parfait, à dimanche.\nJe passe vers midi.", Style.Aucun, false) } },
        };

        var resume = new Resume
        {
            Conv = conv, Dossier = "Famille Martin", Messages = messages.Count(m => m.Evenement == null), Lignes = messages.Count,
            Premier = Outils.Local(messages[0].Date), Dernier = Outils.Local(messages[^1].Date), ExporteLe = maintenant,
            Annees = { (2026, messages.Count) }, Dates = messages.Select(m => m.Date).ToHashSet(),
        };
        using (var html = new SortieHtml(dossier, resume))
        using (var texte = new SortieTexte(dossier, resume))
        using (var json = new SortieJson(dossier, resume))
        {
            foreach (var m in messages) { html.Ecrire(m); texte.Ecrire(m); json.Ecrire(m); }
        }
        var index = Path.Combine(destination, "index.html");
        SortieHtml.EcrireIndex(index, new List<Resume> { resume }, maintenant, pages: true);
        return index;
    }

    static void Image(string chemin, int largeur, int hauteur, Color couleur, string texte)
    {
        using var image = new Bitmap(largeur, hauteur);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(couleur);
            using var police = new Font("Segoe UI", 26f);
            g.DrawString(texte, police, Brushes.White, 24, 20);
        }
        image.Save(chemin, ImageFormat.Png);
    }

    /// <summary>Enregistre une image de la fenêtre remplie de conversations fictives.</summary>
    public static void Capturer(string chemin)
    {
        using var fenetre = new Fenetre(Conversations());
        fenetre.Show();
        Application.DoEvents();
        using var image = new Bitmap(fenetre.Width, fenetre.Height);
        fenetre.DrawToBitmap(image, new Rectangle(0, 0, fenetre.Width, fenetre.Height));
        image.Save(chemin, ImageFormat.Png);
        fenetre.Close();
    }
}

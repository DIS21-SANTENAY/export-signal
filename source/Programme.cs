using System.Globalization;
using System.Runtime.InteropServices;

namespace ExportSignal;

/// <summary>
/// Point d'entrée. Sans argument : la fenêtre. Avec arguments : export en ligne de commande,
/// pratique pour une tâche planifiée (voir --aide).
/// </summary>
static class Programme
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processus);

    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Outils.Fr;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Outils.Fr;

        if (args.Length > 0)
        {
            AttachConsole(-1);   // écrit dans la console qui a lancé l'outil, s'il y en a une
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
            try
            {
                // Contrôle de l'aspect avec des données fictives : --demo DOSSIER
                if (args[0] == "--demo" && args.Length == 2)
                {
                    Console.WriteLine(Demonstration.Ecrire(Path.GetFullPath(args[1])));
                    ApplicationConfiguration.Initialize();
                    Demonstration.Capturer(Path.Combine(Path.GetFullPath(args[1]), "fenetre.png"));
                    return 0;
                }
                return LigneDeCommande(args);
            }
            catch (ErreurSignal e)
            {
                Console.Error.WriteLine("Erreur : " + e.Message);
                return 2;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("Une erreur inattendue est survenue :\n\n" + e.Exception.Message, "Export Signal",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Application.Run(new Fenetre());
        return 0;
    }

    const string Aide = """
        Export Signal : exporte les conversations de Signal Desktop de cette session Windows.

          ExportSignal.exe                      ouvre la fenêtre
          ExportSignal.exe --liste              affiche les conversations
          ExportSignal.exe --export DOSSIER     exporte dans DOSSIER

        Options de --export :
          --conv TEXTE        seulement les conversations dont le nom contient TEXTE (option répétable)
          --txt, --json       ajoute les formats texte et JSON
          --sans-html         n'écrit pas les pages web
          --sans-fichiers     n'exporte pas les photos, vidéos et fichiers
          --depuis AAAA-MM-JJ, --jusqu AAAA-MM-JJ   limite la période
          --profil DOSSIER    dossier de données de Signal, s'il n'est pas à l'endroit habituel
        """;

    static int LigneDeCommande(string[] args)
    {
        var options = new Options();
        var filtres = new List<string>();
        string profil = BaseSignal.DossierParDefaut();
        bool liste = false;

        for (int i = 0; i < args.Length; i++)
        {
            string Valeur() => ++i < args.Length ? args[i] : throw new ErreurSignal("Il manque une valeur après " + args[i - 1] + ".");
            switch (args[i].ToLowerInvariant())
            {
                case "--liste": liste = true; break;
                case "--export": options.Destination = Path.GetFullPath(Valeur()); break;
                case "--conv": filtres.Add(Valeur()); break;
                case "--txt": options.Texte = true; break;
                case "--json": options.Json = true; break;
                case "--sans-html": options.Html = false; break;
                case "--sans-fichiers": options.Fichiers = false; break;
                case "--depuis": options.Depuis = Date(Valeur()); break;
                case "--jusqu": options.Jusqu = Date(Valeur()); break;
                case "--profil": profil = Path.GetFullPath(Valeur()); break;
                default:
                    Console.WriteLine(Aide);
                    return args[i] is "--aide" or "-h" or "/?" ? 0 : 1;
            }
        }

        using var source = new BaseSignal(profil);
        var conversations = source.Conversations.Where(c => c.Lignes > 0)
            .Where(c => filtres.Count == 0 || filtres.Any(f => c.Nom.Contains(f, StringComparison.CurrentCultureIgnoreCase) || c.Id == f))
            .OrderByDescending(c => c.Dernier).ToList();

        if (liste || options.Destination.Length == 0)
        {
            foreach (var c in conversations)
                Console.WriteLine($"{Outils.JourCourt(Outils.Local(c.Dernier))}  {c.Messages,7} messages  {Outils.Taille(c.TailleFichiers),9}  {(c.Groupe ? "groupe  " : "personne")}  {c.Nom}");
            Console.WriteLine(Outils.Pluriel(conversations.Count, "conversation", "conversations"));
            return 0;
        }
        if (conversations.Count == 0) throw new ErreurSignal("Aucune conversation ne correspond.");
        if (!options.Html && !options.Texte && !options.Json) throw new ErreurSignal("Aucun format demandé.");

        options.Conversations = conversations;
        var bilan = new Exporteur(source, options, null, CancellationToken.None).Executer();
        Console.WriteLine(Outils.Pluriel(bilan.Conversations, "conversation", "conversations") + ", " + Outils.Pluriel(bilan.Messages, "ligne", "lignes")
                          + ", " + Outils.Pluriel(bilan.Fichiers, "fichier", "fichiers") + ", " + bilan.FichiersAbsents + " absents, " + bilan.Erreurs + " erreurs");
        Console.WriteLine(bilan.Journal);
        return bilan.Erreurs > 0 ? 3 : 0;
    }

    static DateTime Date(string texte) =>
        DateTime.TryParseExact(texte, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new ErreurSignal("Date attendue au format AAAA-MM-JJ : " + texte);
}

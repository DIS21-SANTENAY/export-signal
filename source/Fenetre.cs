using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ExportSignal;

/// <summary>La fenêtre de l'outil : liste des conversations, options, dossier d'export et avancement.</summary>
sealed class Fenetre : Form
{
    // Charte DIS21.
    static readonly Color Marine = Color.FromArgb(0x18, 0x36, 0x5e);
    static readonly Color Bleu = Color.FromArgb(0x32, 0x71, 0xb7);
    static readonly Color BleuPale = Color.FromArgb(0xc9, 0xdc, 0xf0);
    static readonly Color Discret = Color.FromArgb(0x5b, 0x6b, 0x7c);

    BaseSignal? _base;
    CancellationTokenSource? _arret;
    string? _dernierExport;

    readonly Label _etatSignal = new();
    readonly TextBox _recherche = new();
    readonly Button _toutCocher = new(), _toutDecocher = new();
    readonly Label _selection = new();
    readonly DataGridView _grille = new();
    readonly CheckBox _html = new(), _texte = new(), _json = new(), _fichiers = new(), _periode = new();
    readonly DateTimePicker _depuis = new(), _jusqu = new();
    readonly TextBox _destination = new();
    readonly Button _parcourir = new(), _exporter = new(), _annuler = new(), _ouvrir = new();
    readonly ProgressBar _barre = new();
    readonly Label _etat = new();

    const int ColCoche = 0, ColNom = 1, ColMessages = 3, ColFichiers = 4, ColDernier = 6;

    /// <param name="demonstration">Conversations fictives à afficher à la place de celles de Signal (contrôle de l'aspect).</param>
    public Fenetre(List<Conversation>? demonstration = null)
    {
        SuspendLayout();
        Text = "Export Signal";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception) { /* icône par défaut */ }

        Construire();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1060, 720);
        MinimumSize = new Size(860, 560);
        ResumeLayout(false);
        PerformLayout();

        _destination.Text = Path.Combine(DossierParDefaut(), "Export Signal", DateTime.Now.ToString("yyyy-MM-dd"));
        if (demonstration != null)
        {
            Remplir(demonstration);
            _grille.Rows[0].Cells[ColCoche].Value = _grille.Rows[1].Cells[ColCoche].Value = true;
            MettreAJourSelection();
            _exporter.Enabled = false;
            return;
        }
        Shown += async (_, _) => await Charger(BaseSignal.DossierParDefaut());
        FormClosing += (_, e) =>
        {
            if (_arret != null)
            {
                // Un export est en cours : on l'arrête proprement, la fenêtre se fermera juste après.
                e.Cancel = true;
                _fermerApres = true;
                _arret.Cancel();
                _etat.Text = "Arrêt en cours";
                return;
            }
            _base?.Dispose();
        };
    }

    bool _fermerApres;

    /// <summary>
    /// Dossier proposé pour l'export : Documents, sauf s'il est synchronisé par OneDrive.
    /// Des conversations privées en clair n'ont pas à partir dans le nuage sans que l'utilisateur l'ait choisi.
    /// </summary>
    static string DossierParDefaut()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool synchronise = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Any(racine => !string.IsNullOrEmpty(racine) && documents.StartsWith(racine, StringComparison.OrdinalIgnoreCase))
            || documents.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
        return synchronise || documents.Length == 0 ? profil : documents;
    }

    // ------------------------------------------------------------------ construction de la fenêtre

    void Construire()
    {
        var racine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        racine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        racine.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        racine.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        racine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        racine.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        racine.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        racine.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Bandeau de titre.
        var bandeau = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Marine, Padding = new Padding(18, 12, 18, 14), Margin = Padding.Empty,
        };
        bandeau.Controls.Add(new Label
        {
            Text = "Export Signal", AutoSize = true, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 2),
        });
        _etatSignal.AutoSize = true;
        _etatSignal.ForeColor = BleuPale;
        _etatSignal.Text = "Lecture de Signal en cours";
        _etatSignal.Margin = new Padding(2, 0, 0, 0);
        bandeau.Controls.Add(_etatSignal);
        racine.Controls.Add(bandeau, 0, 0);

        // Recherche et sélection.
        var barre = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(16, 12, 16, 8), Margin = Padding.Empty };
        barre.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        barre.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        barre.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        barre.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _recherche.PlaceholderText = "Rechercher une conversation";
        _recherche.Dock = DockStyle.Fill;
        _recherche.Margin = new Padding(0, 4, 10, 3);
        _recherche.TextChanged += (_, _) => Filtrer();
        Bouton(_toutCocher, "Tout cocher");
        Bouton(_toutDecocher, "Tout décocher");
        _toutCocher.Click += (_, _) => CocherVisibles(true);
        _toutDecocher.Click += (_, _) => CocherVisibles(false);
        _selection.AutoSize = true;
        _selection.Anchor = AnchorStyles.Right;
        _selection.ForeColor = Discret;
        barre.Controls.Add(_recherche, 0, 0);
        barre.Controls.Add(_toutCocher, 1, 0);
        barre.Controls.Add(_toutDecocher, 2, 0);
        barre.Controls.Add(_selection, 3, 0);
        racine.Controls.Add(barre, 0, 1);

        ConstruireGrille();
        racine.Controls.Add(_grille, 0, 2);

        // Options.
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(16, 10, 16, 0), Margin = Padding.Empty };
        options.Controls.Add(Libelle("Exporter en"));
        Case(_html, "Pages web", true);
        Case(_texte, "Texte", false);
        Case(_json, "JSON", false);
        Case(_fichiers, "Avec les photos, vidéos et fichiers", true);
        _fichiers.Margin = new Padding(22, 3, 3, 3);
        Case(_periode, "Seulement du", false);
        _periode.Margin = new Padding(22, 3, 0, 3);
        foreach (var date in new[] { _depuis, _jusqu })
        {
            date.Format = DateTimePickerFormat.Short;
            date.Width = 118;
            date.Enabled = false;
        }
        _depuis.Value = DateTime.Today.AddYears(-1);
        _jusqu.Value = DateTime.Today;
        _periode.CheckedChanged += (_, _) => _depuis.Enabled = _jusqu.Enabled = _periode.Checked;
        options.Controls.AddRange(new Control[] { _html, _texte, _json, _fichiers, _periode, _depuis, Libelle("au"), _jusqu });
        racine.Controls.Add(options, 0, 3);

        // Dossier d'export.
        var dossier = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2, Padding = new Padding(16, 6, 16, 0), Margin = Padding.Empty };
        dossier.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dossier.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dossier.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _destination.Dock = DockStyle.Fill;
        _destination.Margin = new Padding(6, 4, 8, 3);
        Bouton(_parcourir, "Parcourir");
        _parcourir.Click += (_, _) => ChoisirDossier();
        dossier.Controls.Add(Libelle("Dossier d'export"), 0, 0);
        dossier.Controls.Add(_destination, 1, 0);
        dossier.Controls.Add(_parcourir, 2, 0);
        var avis = new Label
        {
            Text = "L'export n'est pas chiffré : toute personne qui ouvre ce dossier peut lire vos conversations. Rangez-le dans un endroit sûr.",
            AutoSize = true, ForeColor = Discret, Margin = new Padding(6, 2, 0, 0),
        };
        dossier.Controls.Add(avis, 1, 1);
        dossier.SetColumnSpan(avis, 2);
        racine.Controls.Add(dossier, 0, 4);

        // Avancement et boutons.
        var bas = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 2, Padding = new Padding(16, 12, 16, 14), Margin = Padding.Empty };
        bas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bas.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bas.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bas.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _barre.Dock = DockStyle.Fill;
        _barre.Maximum = 1000;
        _barre.Height = 14;
        _barre.Margin = new Padding(3, 8, 14, 2);
        _etat.AutoSize = true;
        _etat.ForeColor = Discret;
        _etat.Margin = new Padding(3, 0, 0, 0);
        Bouton(_ouvrir, "Ouvrir l'export");
        Bouton(_annuler, "Annuler");
        Bouton(_exporter, "Exporter");
        _exporter.BackColor = Bleu;
        _exporter.ForeColor = Color.White;
        _exporter.FlatStyle = FlatStyle.Flat;
        _exporter.FlatAppearance.BorderSize = 0;
        _exporter.Padding = new Padding(18, 4, 18, 4);
        _ouvrir.Enabled = _annuler.Enabled = _exporter.Enabled = false;
        _ouvrir.Click += (_, _) => Ouvrir();
        _annuler.Click += (_, _) => { _arret?.Cancel(); _annuler.Enabled = false; _etat.Text = "Arrêt en cours"; };
        _exporter.Click += async (_, _) => await Exporter();
        bas.Controls.Add(_barre, 0, 0);
        bas.Controls.Add(_etat, 0, 1);
        bas.Controls.Add(_ouvrir, 1, 0);
        bas.Controls.Add(_annuler, 2, 0);
        bas.Controls.Add(_exporter, 3, 0);
        foreach (var b in new[] { _ouvrir, _annuler, _exporter }) bas.SetRowSpan(b, 2);
        racine.Controls.Add(bas, 0, 5);

        Controls.Add(racine);
    }

    void ConstruireGrille()
    {
        var g = _grille;
        g.Dock = DockStyle.Fill;
        g.Margin = new Padding(16, 0, 16, 0);
        g.ReadOnly = true;
        g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false;
        g.RowHeadersVisible = false;
        g.MultiSelect = true;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.BackgroundColor = Color.White;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Color.FromArgb(0xe3, 0xea, 0xf2);
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0xf2, 0xf6, 0xfa);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Marine;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(0xf2, 0xf6, 0xfa);
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Marine;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(2, 5, 2, 5);
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xdc, 0xe9, 0xf7);
        g.DefaultCellStyle.SelectionForeColor = Color.Black;
        g.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
        g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        g.StandardTab = true;

        float k = DeviceDpi / 96f;
        g.RowTemplate.Height = (int)(28 * k);
        var droite = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, Format = "N0", FormatProvider = Outils.Fr };
        var jour = new DataGridViewCellStyle { Format = "dd/MM/yyyy", FormatProvider = Outils.Fr };
        g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = (int)(38 * k), Resizable = DataGridViewTriState.False, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Conversation", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = (int)(180 * k) });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Width = (int)(90 * k) });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Messages", Width = (int)(95 * k), DefaultCellStyle = droite, ValueType = typeof(long) });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fichiers", Width = (int)(95 * k), ValueType = typeof(long), DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Premier message", Width = (int)(130 * k), DefaultCellStyle = jour, ValueType = typeof(DateTime) });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Dernier message", Width = (int)(130 * k), DefaultCellStyle = jour, ValueType = typeof(DateTime) });

        // Un clic n'importe où sur la ligne coche ou décoche la conversation ; la barre d'espace aussi.
        g.CellClick += (_, e) => { if (e.RowIndex >= 0) Basculer(g.Rows[e.RowIndex]); };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space) return;
            foreach (DataGridViewRow ligne in g.SelectedRows) Basculer(ligne, maj: false);
            MettreAJourSelection();
            e.Handled = true;
        };
        // La colonne des fichiers contient une taille en octets, affichée en Ko, Mo ou Go.
        g.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == ColFichiers && e.Value is long octets)
            {
                e.Value = octets == 0 ? "" : Outils.Taille(octets);
                e.FormattingApplied = true;
            }
        };
    }

    static Label Libelle(string texte) => new() { Text = texte, AutoSize = true, Margin = new Padding(0, 7, 6, 0) };

    static void Case(CheckBox c, string texte, bool cochee)
    {
        c.Text = texte;
        c.AutoSize = true;
        c.Checked = cochee;
        c.Margin = new Padding(6, 5, 3, 3);
    }

    static void Bouton(Button b, string texte)
    {
        b.Text = texte;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(10, 4, 10, 4);
        b.Margin = new Padding(4, 2, 0, 2);
        b.UseVisualStyleBackColor = true;
    }

    // ------------------------------------------------------------------ chargement de Signal

    async Task Charger(string dossier)
    {
        UseWaitCursor = true;
        try
        {
            _base = await Task.Run(() => new BaseSignal(dossier));
        }
        catch (ErreurSignal e)
        {
            UseWaitCursor = false;
            if (!BaseSignal.EstUnDossierSignal(dossier))
            {
                var reponse = MessageBox.Show(this,
                    "Signal Desktop n'a pas été trouvé sur cette session Windows.\n\n"
                    + "L'outil lit les conversations de Signal installé sur cet ordinateur (et non celles du téléphone).\n\n"
                    + "Voulez-vous indiquer vous-même le dossier de données de Signal ?",
                    "Export Signal", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (reponse == DialogResult.Yes)
                {
                    using var choix = new FolderBrowserDialog { Description = "Dossier de données de Signal Desktop", UseDescriptionForTitle = true };
                    if (choix.ShowDialog(this) == DialogResult.OK) { await Charger(choix.SelectedPath); return; }
                }
                _etatSignal.Text = "Signal Desktop introuvable";
                return;
            }
            _etatSignal.Text = "Lecture de Signal impossible";
            MessageBox.Show(this, e.Message, "Export Signal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }
        Remplir(_base.Conversations);
    }

    void Remplir(List<Conversation> toutes)
    {
        var conversations = toutes.Where(c => c.Lignes > 0).ToList();
        _grille.Rows.Clear();
        foreach (var c in conversations)
        {
            int i = _grille.Rows.Add(false, c.Nom, c.Groupe ? "Groupe" : "Personne", c.Messages, c.TailleFichiers,
                Outils.Local(c.Premier), Outils.Local(c.Dernier));
            _grille.Rows[i].Tag = c;
        }
        _grille.Sort(_grille.Columns[ColDernier], System.ComponentModel.ListSortDirection.Descending);
        _grille.ClearSelection();
        _etatSignal.Text = Outils.Pluriel(conversations.Count, "conversation", "conversations") + ", "
                           + Outils.Pluriel(conversations.Sum(c => c.Messages), "message", "messages") + ", "
                           + Outils.Taille(conversations.Sum(c => c.TailleFichiers)) + " de fichiers. Signal peut rester ouvert, rien n'y est modifié.";
        _exporter.Enabled = true;
        MettreAJourSelection();
        _recherche.Focus();
    }

    // ------------------------------------------------------------------ sélection

    static bool Cochee(DataGridViewRow ligne) => ligne.Cells[ColCoche].Value is true;

    void Basculer(DataGridViewRow ligne, bool maj = true)
    {
        ligne.Cells[ColCoche].Value = !Cochee(ligne);
        if (maj) MettreAJourSelection();
    }

    void CocherVisibles(bool etat)
    {
        foreach (DataGridViewRow ligne in _grille.Rows)
            if (ligne.Visible) ligne.Cells[ColCoche].Value = etat;
        MettreAJourSelection();
    }

    void Filtrer()
    {
        var texte = _recherche.Text.Trim();
        _grille.CurrentCell = null;   // une ligne courante ne peut pas être masquée
        foreach (DataGridViewRow ligne in _grille.Rows)
            ligne.Visible = texte.Length == 0
                            || (ligne.Cells[ColNom].Value as string ?? "").Contains(texte, StringComparison.CurrentCultureIgnoreCase)
                            || ((ligne.Tag as Conversation)?.Numero ?? "").Contains(texte, StringComparison.OrdinalIgnoreCase);
    }

    List<Conversation> Choisies() =>
        _grille.Rows.Cast<DataGridViewRow>().Where(Cochee).Select(l => (Conversation)l.Tag!).ToList();

    void MettreAJourSelection()
    {
        var choix = Choisies();
        _selection.Text = choix.Count == 0
            ? "Cochez les conversations à exporter"
            : Outils.Pluriel(choix.Count, "conversation cochée", "conversations cochées") + ", "
              + Outils.Pluriel(choix.Sum(c => c.Messages), "message", "messages") + ", "
              + Outils.Taille(choix.Sum(c => c.TailleFichiers)) + " de fichiers";
    }

    // ------------------------------------------------------------------ export

    void ChoisirDossier()
    {
        using var choix = new FolderBrowserDialog { Description = "Dossier où ranger l'export", UseDescriptionForTitle = true };
        var actuel = _destination.Text.Trim();
        while (actuel.Length > 3 && !Directory.Exists(actuel)) actuel = Path.GetDirectoryName(actuel) ?? "";
        if (Directory.Exists(actuel)) choix.InitialDirectory = actuel;
        if (choix.ShowDialog(this) == DialogResult.OK) _destination.Text = choix.SelectedPath;
    }

    void Avertir(string message) =>
        MessageBox.Show(this, message, "Export Signal", MessageBoxButtons.OK, MessageBoxIcon.Information);

    async Task Exporter()
    {
        if (_base == null || _arret != null) return;
        var options = new Options
        {
            Conversations = Choisies(),
            Html = _html.Checked,
            Texte = _texte.Checked,
            Json = _json.Checked,
            Fichiers = _fichiers.Checked,
        };
        if (options.Conversations.Count == 0) { Avertir("Cochez au moins une conversation à exporter."); return; }
        if (!options.Html && !options.Texte && !options.Json) { Avertir("Choisissez au moins un format : pages web, texte ou JSON."); return; }
        if (_periode.Checked)
        {
            options.Depuis = _depuis.Value.Date;
            options.Jusqu = _jusqu.Value.Date;
            if (options.Depuis > options.Jusqu) { Avertir("La date de début est après la date de fin."); return; }
        }
        try
        {
            options.Destination = Path.GetFullPath(_destination.Text.Trim());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Avertir("Le dossier d'export indiqué n'est pas valide.");
            return;
        }

        // Place disponible : les fichiers sont recopiés en clair dans l'export.
        if (options.Fichiers)
        {
            try
            {
                long besoin = options.Conversations.Sum(c => c.TailleFichiers) + (200L << 20);
                var disque = new DriveInfo(Path.GetPathRoot(options.Destination)!);
                if (disque.AvailableFreeSpace < besoin)
                {
                    var suite = MessageBox.Show(this,
                        "Il reste " + Outils.Taille(disque.AvailableFreeSpace) + " sur " + disque.Name + " alors que l'export peut demander jusqu'à "
                        + Outils.Taille(besoin) + ".\n\nContinuer quand même ?", "Export Signal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (suite != DialogResult.Yes) return;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { /* dossier réseau : pas de contrôle */ }
        }

        Activer(false);
        _arret = new CancellationTokenSource();
        var progres = new Progress<Avancement>(a =>
        {
            _barre.Value = (int)Math.Clamp(a.Fraction * 1000, 0, 1000);
            _etat.Text = a.Texte;
        });
        Bilan? bilan = null;
        try
        {
            var source = _base;
            var arret = _arret.Token;
            bilan = await Task.Run(() => new Exporteur(source, options, progres, arret).Executer());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or ErreurSignal)
        {
            _etat.Text = "Export interrompu par une erreur.";
            MessageBox.Show(this, "L'export s'est arrêté sur une erreur :\n\n" + e.Message, "Export Signal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _arret.Dispose();
            _arret = null;
            Activer(true);
        }
        if (_fermerApres) { Close(); return; }
        if (bilan == null || IsDisposed) return;

        _dernierExport = options.Html && File.Exists(bilan.Index) ? bilan.Index : options.Destination;
        _ouvrir.Enabled = true;
        var resume = Outils.Pluriel(bilan.Conversations, "conversation exportée", "conversations exportées") + ", "
                     + Outils.Pluriel(bilan.Messages, "ligne", "lignes")
                     + (options.Fichiers ? ", " + Outils.Pluriel(bilan.Fichiers, "fichier", "fichiers") : "") + ".";
        _etat.Text = (bilan.Annule ? "Export interrompu. " : "Export terminé. ") + resume;
        if (bilan.Annule) return;

        var message = "Export terminé.\n\n" + resume;
        if (bilan.FichiersAbsents > 0)
            message += "\n\n" + Outils.Nombre(bilan.FichiersAbsents) + " fichiers ne sont plus sur cet ordinateur (jamais téléchargés ou effacés par Signal) "
                       + "et sont signalés comme tels dans l'export.";
        if (bilan.Erreurs > 0)
            message += "\n\n" + Outils.Nombre(bilan.Erreurs) + " fichiers n'ont pas pu être lus : le détail est dans journal.txt.";
        message += "\n\nOuvrir l'export maintenant ?";
        if (MessageBox.Show(this, message, "Export Signal", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Ouvrir();
    }

    void Activer(bool actif)
    {
        foreach (Control c in new Control[] { _recherche, _toutCocher, _toutDecocher, _grille, _html, _texte, _json, _fichiers, _periode, _destination, _parcourir, _exporter })
            c.Enabled = actif;
        _depuis.Enabled = _jusqu.Enabled = actif && _periode.Checked;
        _annuler.Enabled = !actif;
        if (!actif) { _barre.Value = 0; _ouvrir.Enabled = false; }
    }

    void Ouvrir()
    {
        if (_dernierExport == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_dernierExport) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Avertir("Impossible d'ouvrir « " + _dernierExport + " ».");
        }
    }
}

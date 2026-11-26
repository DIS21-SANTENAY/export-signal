using System.Security.Cryptography;

namespace ExportSignal;

/// <summary>
/// Remise en clair des fichiers que Signal garde sur le disque.
/// Format « version 2 » : vecteur (16 octets), contenu chiffré en AES-256-CBC, puis empreinte HMAC-SHA256 (32 octets).
/// La clé locale fait 64 octets : 32 pour le chiffrement, 32 pour l'empreinte.
/// Les fichiers plus anciens (sans clé locale) sont déjà en clair.
/// </summary>
static class Dechiffrement
{
    const int TailleVecteur = 16, TailleEmpreinte = 32;

    /// <summary>Écrit le fichier en clair dans la destination (écriture sous un nom provisoire, puis renommage).</summary>
    public static void VersFichier(string source, string? cle64, int? version, long taille, string destination)
    {
        var provisoire = destination + ".partiel";
        try
        {
            using (var sortie = new FileStream(provisoire, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                Extraire(source, cle64, version, taille, sortie);
            File.Move(provisoire, destination, true);
        }
        catch
        {
            try { File.Delete(provisoire); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Lit un petit fichier (texte d'un long message) en mémoire.</summary>
    public static byte[] EnMemoire(string source, string? cle64, int? version, long taille)
    {
        using var sortie = new MemoryStream();
        Extraire(source, cle64, version, taille, sortie);
        return sortie.ToArray();
    }

    /// <summary>
    /// Premiers octets du fichier en clair : suffisent à reconnaître son vrai format (JPEG, PNG, MP4...)
    /// sans le déchiffrer en entier. Tableau vide si le fichier est trop court.
    /// </summary>
    public static byte[] Debut(string source, string? cle64, int? version)
    {
        using var entree = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var tampon = new byte[TailleVecteur + 16];
        int lu = entree.ReadAtLeast(tampon, tampon.Length, throwOnEndOfStream: false);
        if (!Chiffre(cle64, version)) return tampon[..Math.Min(lu, 16)];
        if (lu < tampon.Length) return Array.Empty<byte>();
        var cle = Convert.FromBase64String(cle64!);
        if (cle.Length != 64) return Array.Empty<byte>();
        using var aes = Aes.Create();
        aes.Key = cle[..32];
        return aes.DecryptCbc(tampon.AsSpan(TailleVecteur, 16), tampon.AsSpan(0, TailleVecteur), PaddingMode.None);
    }

    /// <summary>Taille attendue du fichier en clair, pour ne pas refaire un export déjà présent.</summary>
    public static long TailleAttendue(string source, string? cle64, int? version, long taille) =>
        Chiffre(cle64, version) ? taille : new FileInfo(source).Length;

    static bool Chiffre(string? cle64, int? version) => !string.IsNullOrEmpty(cle64) && version is >= 2;

    static void Extraire(string source, string? cle64, int? version, long taille, Stream sortie)
    {
        using var entree = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.SequentialScan);
        if (!Chiffre(cle64, version))
        {
            entree.CopyTo(sortie);
            return;
        }

        var cle = Convert.FromBase64String(cle64!);
        long total = entree.Length;
        if (cle.Length != 64 || total < TailleVecteur + TailleEmpreinte + 16)
            throw new InvalidDataException("fichier chiffré ou clé de taille inattendue");

        // Premier passage : on vérifie l'empreinte avant de déchiffrer quoi que ce soit.
        var tampon = new byte[1 << 16];
        using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, cle.AsSpan(32, 32)))
        {
            long reste = total - TailleEmpreinte;
            while (reste > 0)
            {
                int lu = entree.Read(tampon, 0, (int)Math.Min(tampon.Length, reste));
                if (lu <= 0) throw new EndOfStreamException("fichier tronqué");
                hmac.AppendData(tampon, 0, lu);
                reste -= lu;
            }
            var empreinte = new byte[TailleEmpreinte];
            entree.ReadExactly(empreinte);
            if (!CryptographicOperations.FixedTimeEquals(hmac.GetHashAndReset(), empreinte))
                throw new InvalidDataException("empreinte incorrecte, fichier abîmé");
        }

        // Second passage : déchiffrement, en ne gardant que la taille réelle (Signal ajoute du remplissage).
        entree.Position = 0;
        var vecteur = new byte[TailleVecteur];
        entree.ReadExactly(vecteur);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = cle[..32];
        aes.IV = vecteur;
        using var borne = new FluxBorne(entree, total - TailleVecteur - TailleEmpreinte);
        using var clair = new CryptoStream(borne, aes.CreateDecryptor(), CryptoStreamMode.Read);
        long aEcrire = taille > 0 ? taille : long.MaxValue;
        while (aEcrire > 0)
        {
            int lu = clair.Read(tampon, 0, (int)Math.Min(tampon.Length, aEcrire));
            if (lu <= 0) break;
            sortie.Write(tampon, 0, lu);
            aEcrire -= lu;
        }
    }

    /// <summary>Ne laisse lire qu'un nombre donné d'octets du flux sous-jacent.</summary>
    sealed class FluxBorne : Stream
    {
        readonly Stream _source;
        long _reste;
        public FluxBorne(Stream source, long longueur) { _source = source; _reste = longueur; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reste <= 0) return 0;
            int lu = _source.Read(buffer, offset, (int)Math.Min(count, _reste));
            _reste -= lu;
            return lu;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

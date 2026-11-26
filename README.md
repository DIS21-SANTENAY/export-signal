# Export Signal

Exporte les conversations de Signal Desktop (celui installé sur ce PC) en pages web lisibles hors ligne,
avec les photos, vidéos, messages vocaux et fichiers. Texte brut et JSON en option.

## Utilisation

Télécharger `ExportSignal.exe` dans les Releases du dépôt (un seul fichier, rien à installer).
Le lancer, cocher les conversations, choisir le dossier, cliquer sur Exporter.
Ouvrir ensuite `index.html` dans le dossier d'export.

- Signal peut rester ouvert : la base est lue en lecture seule, rien n'est modifié dans Signal.
- L'outil ne fonctionne que dans la session Windows de la personne qui utilise Signal : la clé de la base
  est protégée par Windows pour cette session.
- L'export n'est pas chiffré. Le ranger dans un endroit sûr, hors d'un dossier synchronisé si possible.
- Relancer un export dans le même dossier ne recopie pas les fichiers déjà présents.
- Les conversations de plus de 4 000 lignes sont découpées en une page par année.
- Seuls les fichiers encore présents sur le PC sont exportés ; les autres sont signalés dans la page et
  comptés dans `journal.txt`.

## Ligne de commande

    ExportSignal.exe --liste
    ExportSignal.exe --export D:\Export --conv "Nom" --txt --json --depuis 2025-01-01
    ExportSignal.exe --aide

## Code

| Fichier | Rôle |
| --- | --- |
| `source\BaseSignal.cs` | clé de la base (DPAPI puis AES-GCM), ouverture SQLCipher en lecture seule, requêtes |
| `source\Dechiffrement.cs` | remise en clair des pièces jointes (AES-256-CBC, empreinte HMAC-SHA256) |
| `source\Interprete.cs` | lecture d'un message : texte mis en forme, citation, réactions, événements |
| `source\Exporteur.cs` | déroulement de l'export, nommage des fichiers, journal |
| `source\SortieHtml.cs` | pages web (style et script inclus) |
| `source\SortiesTexteJson.cs` | sorties texte et JSON |
| `source\Fenetre.cs` | la fenêtre |
| `source\Demonstration.cs` | jeu d'essai fictif : `ExportSignal.exe --demo DOSSIER` |

Recompiler : `.\compiler.ps1` (SDK .NET 8). L'exécutable produit contient tout, rien à installer.

Si une mise à jour de Signal change la structure de sa base, c'est `BaseSignal.cs` qu'il faut adapter
(tables `messages`, `message_attachments`, `conversations`, `callsHistory`). Écrit pour Signal Desktop 8.36.

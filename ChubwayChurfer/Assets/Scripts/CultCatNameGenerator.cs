using UnityEngine;

/// <summary>
/// Provides the validated pool of iconic cat names used for leaderboard players.
/// </summary>
public static class CultCatNameGenerator
{
    public static readonly string[] Names =
    {
        "Garfield",
        "Boule de Neige II",
        "Tom",
        "Grosminet",
        "Félix",
        "Oggy",
        "Azraël",
        "Salem",
        "Le Chat du Cheshire",
        "Le Chat Potté",
        "Lucifer",
        "Pattenrond",
        "Grumpy Cat",
        "Nyan Cat"
    };

    public static string GetRandomName(string nameToAvoid = null)
    {
        if (Names.Length == 0)
            return "Garfield";

        string selectedName;
        do
        {
            selectedName = Names[Random.Range(0, Names.Length)];
        }
        while (Names.Length > 1 && selectedName == nameToAvoid);

        return selectedName;
    }

    public static bool ReplaceLegacyNames(PlayerData playerData)
    {
        bool changed = false;
        string previousAssignedName = null;

        for (int i = 0; i < playerData.highscores.Count; ++i)
        {
            HighscoreEntry entry = playerData.highscores[i];
            if (string.IsNullOrWhiteSpace(entry.name) || entry.name == "Trash Cat")
            {
                entry.name = GetRandomName(previousAssignedName);
                previousAssignedName = entry.name;
                playerData.highscores[i] = entry;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(playerData.previousName) || playerData.previousName == "Trash Cat")
        {
            playerData.previousName = GetRandomName(previousAssignedName);
            changed = true;
        }

        return changed;
    }
}

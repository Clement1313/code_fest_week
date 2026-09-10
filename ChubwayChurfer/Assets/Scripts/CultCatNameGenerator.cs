using UnityEngine;

/// <summary>
/// Provides the validated pool of iconic cat names used for leaderboard players.
/// </summary>
public static class CultCatNameGenerator
{
    public const int MaxNameLength = 12;

    public static readonly string[] Names =
    {
        "Minou",
        "Félix",
        "Grisou",
        "Luna",
        "Simba",
        "Nala",
        "Milo",
        "Choupette",
        "Oscar",
        "Bella",
        "Tigrou",
        "Shadow",
        "Câlin",
        "Sushi",
        "Mocha",
        "Mistigri",
        "Pacha",
        "Cookie",
        "Noisette",
        "Saphir",
        "Garfield",
        "Zorro",
        "Plume",
        "Fripouille",
        "Caramel",
        "Loki",
        "Panthère",
        "Neige",
        "Perle",
        "Nuage",
        "Blizzard",
        "Flocon",
        "Lune",
        "Glace",
        "Coton",
        "Ivoire",
        "Albâtre",
        "Dove",
        "Fantôme",
        "Crystal",
        "Polar",
        "Lait",
        "Noir",
        "Ébène",
        "Onyx",
        "Sombra",
        "Nuit",
        "Charcoal",
        "Licorice",
        "Minuit",
        "Raven",
        "Panther",
        "Obsidienne",
        "Velvet",
        "Cosmos",
        "Grizou",
        "Cendre",
        "Brume",
        "Argent",
        "Plomb",
        "Fumée",
        "Steel",
        "Griseille",
        "Fumet",
        "Mistral",
        "Perle Grise",
        "Acier",
        "Zéphyr",
        "Moka",
        "Cannelle",
        "Miel",
        "Ginger",
        "Rufus",
        "Pumpkin",
        "Soleil",
        "Amber",
        "Pêche",
        "Cuivre",
        "Tigré",
        "Piment",
        "Brandy",
        "Fanta",
        "Saffron",
        "Vanille",
        "Biscuit",
        "Nougat",
        "Chiffon",
        "Champagne",
        "Mousse",
        "Crème",
        "Latte",
        "Sorbet",
        "Sand",
        "Tofu",
        "Panna Cotta",
        "Macaron",
        "Marshmallow",
        "Olive",
        "Raisin",
        "Pesto",
        "Chocolat",
        "Crêpe",
        "Coco",
        "Tom",
        "Salem",
        "Crookshanks",
        "Cheshire Cat",
        "Figaro",
        "Oliver",
        "Duchess",
        "Marie",
        "Berlioz",
        "Toulouse",
        "Lucifer",
        "Dinah",
        "Catbus",
        "Goose",
        "Jonesy",
        "Church",
        "Keanu",
        "Mr. Tinkles",
        "Buttercup",
        "Snowbell",
        "Binx",
        "T'Challa",
        "DC",
        "Pyewacket",
        "Sylvester",
        "Felix",
        "Top Cat",
        "Stimpy",
        "Hello Kitty",
        "Scratchy",
        "Snowball II",
        "Snowball V",
        "Gumball",
        "Chi",
        "Nermal",
        "Heathcliff",
        "Azrael",
        "Scratch",
        "Krazy Kat",
        "Pink Panther",
        "Doraemon",
        "Artemis",
        "Diana",
        "Jiji",
        "Happy",
        "Carla",
        "Kirara",
        "Meowth",
        "Espurr",
        "Glameow",
        "Purrloin",
        "Skitty",
        "Shinx",
        "Litten",
        "Torracat",
        "Sprigatito",
        "Meowscarada",
        "Baron",
        "Muta",
        "Yoruichi",
        "Mao",
        "Arthur",
        "Cat Mario",
        "Cat Luigi",
        "Blinx",
        "Morgana",
        "Palico",
        "Mae",
        "Rover",
        "Raymond",
        "K.K. Slider",
        "Tom Nook",
        "Stray",
        "Khajiit",
        "Felicia",
        "Cait Sith",
        "Le Chat",
        "Mog",
        "Macavity",
        "Griddlebone",
        "Tibbles",
        "Grumpy Cat",
        "Nyan Cat",
        "Lil Bub",
        "Maru",
        "Keyboard Cat",
        "Garfi",
        "Venus",
        "Bob the Cat",
        "Tama",
        "Grosminet",
        "Oggy",
        "Azraël",
        "Pattenrond"
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
            if (string.IsNullOrWhiteSpace(entry.name) || entry.name == "Trash Cat" ||
                entry.name.Length > MaxNameLength)
            {
                entry.name = GetRandomName(previousAssignedName);
                previousAssignedName = entry.name;
                playerData.highscores[i] = entry;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(playerData.previousName) || playerData.previousName == "Trash Cat" ||
            playerData.previousName.Length > MaxNameLength)
        {
            playerData.previousName = GetRandomName(previousAssignedName);
            changed = true;
        }

        return changed;
    }
}

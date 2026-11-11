using System;

namespace SteamEarth.Population;

/// <summary>Pays d'origine des habitants étrangers (et des Suisses naturalisés).</summary>
public sealed record Country(string Code, string Fr, string En, int Group, double Weight, string Pool, int SkinMin, int SkinMax);

/// <summary>
/// Prénoms et noms courants. Suisses : selon la langue de la commune et la génération (prénoms à la mode à
/// l'année de naissance) ; étrangers : selon le pays. Les poids des pays suivent à peu près la population
/// résidante étrangère de la Suisse (OFS, 2024) ; ils sont ajustés selon la région linguistique.
/// </summary>
public static class Names
{
    // ---------------------------------------------------------------- Suisses
    // [langue][génération : 0 = née avant 1965, 1 = 1965-1994, 2 = 1995 et après] → prénoms
    static readonly string[][][] SwissMale =
    {
        new[] { // allemand
            new[] { "Hans", "Peter", "Walter", "Ernst", "Fritz", "Kurt", "Werner", "Heinz", "Rudolf", "Max", "Bruno", "Urs", "Beat", "Paul", "Josef", "Hansruedi", "Ueli", "Kaspar" },
            new[] { "Daniel", "Thomas", "Christian", "Stefan", "Michael", "Martin", "Andreas", "Marco", "Patrick", "Reto", "Roger", "Simon", "Lukas", "Matthias", "Adrian", "Philipp", "Pascal", "Fabian" },
            new[] { "Noah", "Liam", "Luca", "Leon", "Elias", "Matteo", "Gabriel", "Nico", "Levin", "Jonas", "Samuel", "Jan", "Julian", "Tim", "Nils", "Lio", "Aaron", "Finn" },
        },
        new[] { // français
            new[] { "Jean", "Pierre", "Michel", "André", "Jacques", "Claude", "Bernard", "Roland", "Gérard", "Marcel", "René", "Charles", "Raymond", "Jean-Pierre", "Serge", "Gilbert" },
            new[] { "Nicolas", "Olivier", "Sébastien", "Christophe", "Laurent", "David", "Frédéric", "Stéphane", "Julien", "Yves", "Cédric", "Vincent", "Alexandre", "Fabien", "Raphaël", "Damien" },
            new[] { "Gabriel", "Liam", "Noah", "Arthur", "Louis", "Nathan", "Léo", "Adam", "Hugo", "Théo", "Lucas", "Mathis", "Jules", "Elias", "Maxime", "Timothée" },
        },
        new[] { // italien
            new[] { "Giuseppe", "Mario", "Franco", "Giovanni", "Carlo", "Luigi", "Renato", "Bruno", "Sergio", "Angelo", "Aldo", "Gianni", "Fulvio", "Remo" },
            new[] { "Marco", "Andrea", "Paolo", "Davide", "Luca", "Stefano", "Fabio", "Roberto", "Massimo", "Alessandro", "Daniele", "Nicola", "Michele", "Simone" },
            new[] { "Leonardo", "Matteo", "Luca", "Alessandro", "Lorenzo", "Tommaso", "Gabriele", "Riccardo", "Edoardo", "Samuele", "Diego", "Mattia", "Elia", "Filippo" },
        },
        new[] { // romanche
            new[] { "Gion", "Duri", "Men", "Jachen", "Rest", "Flurin", "Gieri", "Placi", "Peider", "Linard", "Mattias", "Andri" },
            new[] { "Gian", "Flurin", "Andri", "Curdin", "Reto", "Mario", "Claudio", "Gieri", "Ursin", "Corsin", "Mauro", "Duri" },
            new[] { "Andri", "Gian", "Flurin", "Nino", "Livio", "Elia", "Curdin", "Ursin", "Mauro", "Noah", "Luca", "Nevin" },
        },
    };

    static readonly string[][][] SwissFemale =
    {
        new[] {
            new[] { "Ursula", "Margrit", "Ruth", "Verena", "Elisabeth", "Rosmarie", "Heidi", "Anna", "Marianne", "Gertrud", "Erika", "Silvia", "Monika", "Vreni", "Therese", "Beatrice" },
            new[] { "Sandra", "Nicole", "Andrea", "Claudia", "Barbara", "Daniela", "Karin", "Stefanie", "Sarah", "Melanie", "Corinne", "Simone", "Sabrina", "Jasmin", "Manuela", "Michelle" },
            new[] { "Mia", "Emma", "Mila", "Sofia", "Lina", "Lea", "Elena", "Emilia", "Lara", "Laura", "Anna", "Nina", "Leonie", "Alina", "Lia", "Malea", "Lena", "Ella" },
        },
        new[] {
            new[] { "Marie", "Monique", "Josiane", "Françoise", "Jacqueline", "Danielle", "Claudine", "Suzanne", "Madeleine", "Nicole", "Christiane", "Simone", "Lucienne", "Yvette", "Marguerite" },
            new[] { "Valérie", "Isabelle", "Sandrine", "Nathalie", "Sophie", "Céline", "Stéphanie", "Laurence", "Aurélie", "Mélanie", "Virginie", "Caroline", "Émilie", "Séverine", "Delphine" },
            new[] { "Emma", "Alice", "Louise", "Chloé", "Léa", "Zoé", "Inès", "Mila", "Lina", "Camille", "Juliette", "Jade", "Margaux", "Manon", "Lou", "Eva" },
        },
        new[] {
            new[] { "Maria", "Anna", "Rosa", "Giovanna", "Carla", "Lucia", "Franca", "Teresa", "Elena", "Angela", "Rita", "Graziella", "Ida", "Bruna" },
            new[] { "Francesca", "Chiara", "Sara", "Laura", "Elisa", "Monica", "Paola", "Silvia", "Barbara", "Valentina", "Daniela", "Manuela", "Cristina", "Simona" },
            new[] { "Sofia", "Giulia", "Aurora", "Alice", "Emma", "Giorgia", "Martina", "Ginevra", "Beatrice", "Chiara", "Anna", "Noemi", "Gaia", "Matilde" },
        },
        new[] {
            new[] { "Mengia", "Uorschla", "Turitg", "Barla", "Neta", "Mierta", "Flurina", "Anna", "Mariuschla", "Ladina", "Seraina", "Margrita" },
            new[] { "Ladina", "Seraina", "Flurina", "Corina", "Claudia", "Bigna", "Annina", "Mirta", "Nadia", "Martina", "Romina", "Silvana" },
            new[] { "Ladina", "Seraina", "Lea", "Flurina", "Alina", "Lina", "Nina", "Ronja", "Mia", "Annina", "Bigna", "Giulia" },
        },
    };

    static readonly string[][] SwissSurnames =
    {
        new[] { "Müller", "Meier", "Schmid", "Keller", "Weber", "Huber", "Schneider", "Meyer", "Steiner", "Fischer", "Gerber", "Brunner", "Baumann", "Frei", "Zimmermann", "Moser", "Widmer", "Wyss", "Graf", "Roth", "Bühler", "Kälin", "Aebischer", "Lehmann", "Bachmann", "Zürcher", "Hofer", "Lüthi", "Wenger", "Sutter", "Bösch", "Marti", "Egli", "Furrer", "Stucki", "Imhof", "Zbinden", "Blaser", "Zaugg", "Rüegg" },
        new[] { "Favre", "Rochat", "Pittet", "Girard", "Bonvin", "Cuendet", "Rey", "Perrin", "Morel", "Jaquet", "Monnier", "Fournier", "Duc", "Chappuis", "Dubois", "Dufour", "Gay", "Python", "Clerc", "Berset", "Rossier", "Bovet", "Golay", "Mottier", "Jaccard", "Chevalley", "Vuilleumier", "Maillard", "Délèze", "Fragnière" },
        new[] { "Bernasconi", "Rossi", "Ferrari", "Bianchi", "Fontana", "Lepori", "Pedrazzini", "Galli", "Cattaneo", "Bassi", "Righetti", "Gianini", "Ghirlanda", "Mariotta", "Pellandini", "Quadri", "Lurati", "Bernardi", "Rusconi", "Moretti" },
        new[] { "Caduff", "Derungs", "Casanova", "Cavelti", "Spescha", "Cathomen", "Tuor", "Capaul", "Cadruvi", "Bundi", "Lozza", "Pult", "Andri", "Sialm", "Cabalzar", "Arpagaus", "Collenberg", "Defuns", "Sac", "Gross" },
    };

    // ---------------------------------------------------------------- étrangers : pays (groupe 1 UE/AELE, 2 reste de l'Europe, 3 hors Europe)
    public static readonly Country[] Countries =
    {
        new("IT", "Italie", "Italy", 1, 340, "it", 1, 3),
        new("DE", "Allemagne", "Germany", 1, 330, "de", 0, 2),
        new("PT", "Portugal", "Portugal", 1, 255, "pt", 1, 3),
        new("FR", "France", "France", 1, 175, "fr", 0, 3),
        new("ES", "Espagne", "Spain", 1, 95, "es", 1, 3),
        new("AT", "Autriche", "Austria", 1, 45, "de", 0, 2),
        new("PL", "Pologne", "Poland", 1, 32, "pl", 0, 1),
        new("RO", "Roumanie", "Romania", 1, 30, "ro", 1, 2),
        new("HR", "Croatie", "Croatia", 1, 28, "sr", 1, 2),
        new("NL", "Pays-Bas", "Netherlands", 1, 22, "nl", 0, 2),
        new("HU", "Hongrie", "Hungary", 1, 22, "hu", 0, 2),
        new("GR", "Grèce", "Greece", 1, 16, "gr", 1, 3),
        new("BE", "Belgique", "Belgium", 1, 14, "fr", 0, 2),
        new("XK", "Kosovo", "Kosovo", 2, 115, "sq", 1, 2),
        new("TR", "Turquie", "Turkey", 2, 72, "tr", 2, 3),
        new("MK", "Macédoine du Nord", "North Macedonia", 2, 60, "sq", 1, 2),
        new("RS", "Serbie", "Serbia", 2, 58, "sr", 1, 2),
        new("UA", "Ukraine", "Ukraine", 2, 60, "uk", 0, 1),
        new("GB", "Royaume-Uni", "United Kingdom", 2, 45, "en", 0, 4),
        new("BA", "Bosnie-Herzégovine", "Bosnia and Herzegovina", 2, 24, "sr", 1, 2),
        new("RU", "Russie", "Russia", 2, 18, "uk", 0, 1),
        new("AL", "Albanie", "Albania", 2, 12, "sq", 1, 2),
        new("ER", "Érythrée", "Eritrea", 3, 40, "ti", 4, 5),
        new("AF", "Afghanistan", "Afghanistan", 3, 32, "fa", 2, 3),
        new("SY", "Syrie", "Syria", 3, 26, "ar", 2, 3),
        new("IN", "Inde", "India", 3, 25, "hi", 3, 4),
        new("US", "États-Unis", "United States", 3, 25, "en", 0, 5),
        new("LK", "Sri Lanka", "Sri Lanka", 3, 24, "ta", 4, 5),
        new("BR", "Brésil", "Brazil", 3, 22, "br", 1, 5),
        new("CN", "Chine", "China", 3, 21, "zh", 1, 2),
        new("MA", "Maroc", "Morocco", 3, 11, "ar", 2, 3),
        new("TN", "Tunisie", "Tunisia", 3, 10, "ar", 2, 3),
        new("IQ", "Irak", "Iraq", 3, 10, "ar", 2, 3),
        new("TH", "Thaïlande", "Thailand", 3, 10, "th", 2, 3),
        new("IR", "Iran", "Iran", 3, 9, "fa", 1, 3),
        new("PH", "Philippines", "Philippines", 3, 9, "ph", 2, 3),
        new("CA", "Canada", "Canada", 3, 8, "en", 0, 4),
        new("SO", "Somalie", "Somalia", 3, 8, "so", 4, 5),
        new("CM", "Cameroun", "Cameroon", 3, 6, "fr", 4, 5),
        new("JP", "Japon", "Japan", 3, 6, "ja", 1, 2),
    };

    /// <summary>Multiplicateur du poids d'un pays selon la langue de la région (plus de Français en Suisse romande…).</summary>
    public static double RegionFactor(Country c, Lang region) => (region, c.Code) switch
    {
        (Lang.Fr, "FR") => 3.2, (Lang.Fr, "PT") => 1.7, (Lang.Fr, "ES") => 1.6, (Lang.Fr, "DE") => 0.3, (Lang.Fr, "AT") => 0.2,
        (Lang.Fr, "CM") => 4, (Lang.Fr, "BE") => 3, (Lang.Fr, "MA") => 2, (Lang.Fr, "TN") => 2, (Lang.Fr, "XK") => 0.6,
        (Lang.It, "IT") => 4.5, (Lang.It, "DE") => 0.4, (Lang.It, "FR") => 0.3, (Lang.It, "PT") => 0.8,
        (Lang.De or Lang.Rm, "DE") => 1.35, (Lang.De or Lang.Rm, "AT") => 1.4, (Lang.De or Lang.Rm, "FR") => 0.25, (Lang.De or Lang.Rm, "PT") => 0.8,
        _ => 1,
    };

    // prénoms masculins, féminins, noms de famille par réservoir
    static readonly System.Collections.Generic.Dictionary<string, (string[] m, string[] f, string[] s)> Pools = new()
    {
        ["it"] = (new[] { "Antonio", "Giuseppe", "Salvatore", "Francesco", "Vincenzo", "Marco", "Luca", "Matteo", "Davide", "Lorenzo" },
                  new[] { "Maria", "Giuseppina", "Anna", "Rosa", "Francesca", "Giulia", "Chiara", "Sara", "Martina", "Alessia" },
                  new[] { "Esposito", "Russo", "Romano", "Colombo", "Ricci", "Marino", "Greco", "Bruno", "Gallo", "Conti", "De Luca", "Costa", "Giordano", "Mancini" }),
        ["de"] = (new[] { "Thomas", "Michael", "Andreas", "Stefan", "Jan", "Florian", "Tobias", "Sebastian", "Maximilian", "Felix" },
                  new[] { "Sabine", "Petra", "Katrin", "Julia", "Anna", "Lisa", "Katharina", "Johanna", "Sophie", "Hannah" },
                  new[] { "Schulz", "Becker", "Hoffmann", "Wagner", "Koch", "Richter", "Klein", "Wolf", "Schröder", "Neumann", "Schwarz", "Krüger", "Hartmann", "Lange" }),
        ["pt"] = (new[] { "José", "João", "António", "Manuel", "Rui", "Paulo", "Tiago", "Diogo", "Rodrigo", "Miguel" },
                  new[] { "Maria", "Ana", "Sofia", "Inês", "Beatriz", "Catarina", "Joana", "Mariana", "Rita", "Carla" },
                  new[] { "Silva", "Santos", "Ferreira", "Pereira", "Oliveira", "Costa", "Rodrigues", "Martins", "Sousa", "Fernandes", "Gonçalves", "Gomes", "Lopes", "Marques" }),
        ["br"] = (new[] { "Lucas", "Gabriel", "Rafael", "Pedro", "Matheus", "Thiago", "Felipe", "Bruno", "Leonardo", "Gustavo" },
                  new[] { "Ana", "Juliana", "Camila", "Fernanda", "Beatriz", "Larissa", "Mariana", "Letícia", "Gabriela", "Aline" },
                  new[] { "Silva", "Santos", "Oliveira", "Souza", "Lima", "Pereira", "Carvalho", "Almeida", "Ribeiro", "Barbosa" }),
        ["fr"] = (new[] { "Nicolas", "Julien", "Thomas", "Maxime", "Antoine", "Pierre", "Romain", "Kevin", "Hugo", "Baptiste" },
                  new[] { "Camille", "Marie", "Julie", "Pauline", "Laura", "Manon", "Sarah", "Léa", "Claire", "Mathilde" },
                  new[] { "Martin", "Bernard", "Thomas", "Petit", "Robert", "Richard", "Durand", "Lefebvre", "Moreau", "Laurent", "Simon", "Michel", "Garcia", "Roux" }),
        ["es"] = (new[] { "Antonio", "José", "Manuel", "Francisco", "David", "Javier", "Daniel", "Carlos", "Pablo", "Alejandro" },
                  new[] { "María", "Carmen", "Ana", "Isabel", "Laura", "Lucía", "Marta", "Elena", "Paula", "Cristina" },
                  new[] { "García", "Fernández", "González", "Rodríguez", "López", "Martínez", "Sánchez", "Pérez", "Gómez", "Martín", "Jiménez", "Ruiz" }),
        ["pl"] = (new[] { "Piotr", "Krzysztof", "Tomasz", "Paweł", "Michał", "Marcin", "Jakub", "Kamil" },
                  new[] { "Anna", "Katarzyna", "Magdalena", "Agnieszka", "Joanna", "Monika", "Natalia", "Zuzanna" },
                  new[] { "Nowak", "Kowalski", "Wiśniewski", "Wójcik", "Kamiński", "Lewandowski", "Zieliński", "Szymański" }),
        ["ro"] = (new[] { "Andrei", "Alexandru", "Mihai", "Ionuț", "Cristian", "Florin", "Gabriel", "Vlad" },
                  new[] { "Maria", "Elena", "Ioana", "Andreea", "Alexandra", "Mihaela", "Cristina", "Raluca" },
                  new[] { "Popescu", "Ionescu", "Popa", "Constantin", "Stan", "Dumitru", "Stoica", "Gheorghe" }),
        ["nl"] = (new[] { "Jan", "Pieter", "Daan", "Bram", "Sander", "Ruben", "Thijs", "Joost" },
                  new[] { "Anne", "Eva", "Sanne", "Lotte", "Femke", "Fleur", "Iris", "Noor" },
                  new[] { "de Jong", "Jansen", "de Vries", "van den Berg", "Bakker", "Visser", "Smit", "Mulder" }),
        ["hu"] = (new[] { "László", "István", "Zoltán", "Gábor", "Bence", "Máté", "Ádám", "Péter" },
                  new[] { "Katalin", "Erzsébet", "Zsófia", "Anna", "Eszter", "Réka", "Nóra", "Dóra" },
                  new[] { "Nagy", "Kovács", "Tóth", "Szabó", "Horváth", "Varga", "Kiss", "Molnár" }),
        ["gr"] = (new[] { "Giorgos", "Dimitris", "Nikos", "Kostas", "Giannis", "Christos", "Panagiotis", "Alexandros" },
                  new[] { "Maria", "Eleni", "Katerina", "Sofia", "Dimitra", "Anna", "Georgia", "Ioanna" },
                  new[] { "Papadopoulos", "Georgiou", "Nikolaou", "Dimitriou", "Papadakis", "Konstantinou", "Ioannou", "Pappas" }),
        ["sq"] = (new[] { "Arben", "Besnik", "Driton", "Fatmir", "Agon", "Arian", "Leon", "Dion", "Albion", "Valon" },
                  new[] { "Arta", "Blerta", "Drita", "Fjolla", "Valentina", "Elira", "Lirie", "Mimoza", "Albana", "Era" },
                  new[] { "Krasniqi", "Gashi", "Berisha", "Morina", "Shala", "Hoxha", "Bytyqi", "Kryeziu", "Rexhepi", "Ahmeti", "Islami", "Hasani" }),
        ["tr"] = (new[] { "Mehmet", "Mustafa", "Ahmet", "Ali", "Hüseyin", "Emre", "Burak", "Can", "Yusuf", "Kerem" },
                  new[] { "Ayşe", "Fatma", "Emine", "Zeynep", "Elif", "Merve", "Esra", "Defne", "Ecrin", "Selin" },
                  new[] { "Yılmaz", "Kaya", "Demir", "Şahin", "Çelik", "Yıldız", "Aydın", "Öztürk", "Arslan", "Doğan" }),
        ["sr"] = (new[] { "Dragan", "Zoran", "Goran", "Milan", "Nikola", "Marko", "Stefan", "Luka", "Ivan", "Dejan" },
                  new[] { "Jelena", "Snežana", "Milica", "Ana", "Marija", "Ivana", "Dragana", "Sara", "Tamara", "Mila" },
                  new[] { "Jovanović", "Petrović", "Nikolić", "Marković", "Đorđević", "Stojanović", "Ilić", "Pavlović", "Horvat", "Kovačević", "Hodžić", "Begić" }),
        ["uk"] = (new[] { "Oleksandr", "Andriy", "Dmytro", "Serhiy", "Ivan", "Maksym", "Artem", "Mykola" },
                  new[] { "Olena", "Natalia", "Iryna", "Oksana", "Anastasia", "Yulia", "Sofia", "Daria" },
                  new[] { "Shevchenko", "Kovalenko", "Bondarenko", "Tkachenko", "Kravchenko", "Ivanov", "Petrenko", "Melnyk", "Smirnov", "Popov" }),
        ["en"] = (new[] { "James", "John", "David", "Michael", "Daniel", "Oliver", "Jack", "Ethan", "Ryan", "Benjamin" },
                  new[] { "Sarah", "Emily", "Emma", "Jessica", "Olivia", "Charlotte", "Amelia", "Grace", "Hannah", "Lucy" },
                  new[] { "Smith", "Jones", "Williams", "Brown", "Taylor", "Wilson", "Johnson", "Davies", "Miller", "Clark", "Walker", "Wright" }),
        ["ti"] = (new[] { "Yonas", "Tesfay", "Kidane", "Mehari", "Samuel", "Filmon", "Habtom", "Dawit" },
                  new[] { "Senait", "Helen", "Lidia", "Saba", "Selam", "Rahwa", "Ruta", "Winta" },
                  new[] { "Tesfamariam", "Gebremedhin", "Haile", "Kidane", "Tekle", "Ghebre", "Woldu", "Berhane" }),
        ["fa"] = (new[] { "Ahmad", "Mohammad", "Ali", "Reza", "Hamid", "Omid", "Amir", "Hossein", "Mehdi", "Sami" },
                  new[] { "Fatima", "Zahra", "Maryam", "Sara", "Shirin", "Parisa", "Laleh", "Nazanin", "Mina", "Roya" },
                  new[] { "Ahmadi", "Hosseini", "Mohammadi", "Rahimi", "Karimi", "Rezaei", "Sultani", "Jafari", "Azizi", "Noori" }),
        ["ar"] = (new[] { "Mohamed", "Ahmed", "Youssef", "Omar", "Karim", "Hassan", "Khaled", "Bilal", "Adam", "Rami" },
                  new[] { "Fatima", "Amina", "Leila", "Nour", "Sara", "Yasmine", "Rania", "Salma", "Hana", "Mariam" },
                  new[] { "Haddad", "Benali", "El Amrani", "Khalil", "Nasser", "Saleh", "Mansour", "Bouazizi", "Hamdan", "Darwish" }),
        ["hi"] = (new[] { "Rahul", "Amit", "Vikram", "Arjun", "Rohan", "Sanjay", "Anil", "Karan" },
                  new[] { "Priya", "Ananya", "Pooja", "Neha", "Divya", "Kavya", "Sneha", "Aisha" },
                  new[] { "Sharma", "Patel", "Singh", "Kumar", "Gupta", "Reddy", "Iyer", "Nair" }),
        ["ta"] = (new[] { "Kumaran", "Suresh", "Rajan", "Tharshan", "Niroshan", "Kajan", "Vithushan", "Sivakumar" },
                  new[] { "Lakshmi", "Tharsika", "Nirosha", "Kajani", "Sivaranjani", "Mathumitha", "Abirami", "Thulasi" },
                  new[] { "Sivarajah", "Thurairajah", "Kanagaratnam", "Selvarajah", "Navaratnam", "Balasingham", "Murugesu", "Ponnampalam" }),
        ["zh"] = (new[] { "Wei", "Hao", "Jun", "Lei", "Jie", "Tao", "Yang", "Ming" },
                  new[] { "Li", "Mei", "Xiu", "Yan", "Jing", "Fang", "Ying", "Lan" },
                  new[] { "Wang", "Li", "Zhang", "Liu", "Chen", "Yang", "Huang", "Zhao", "Wu", "Zhou" }),
        ["th"] = (new[] { "Somchai", "Anan", "Niran", "Kittisak", "Chai", "Prasert" },
                  new[] { "Siriporn", "Malee", "Nittaya", "Ploy", "Kanya", "Supaporn" },
                  new[] { "Saetang", "Srisuk", "Chaiyaporn", "Wongsawat", "Rattanakorn", "Boonmee" }),
        ["ph"] = (new[] { "Jose", "Mark", "John Paul", "Rodel", "Jerome", "Paolo" },
                  new[] { "Maria", "Grace", "Kristine", "Joy", "Mary Ann", "Angelica" },
                  new[] { "Santos", "Reyes", "Cruz", "Bautista", "Garcia", "Mendoza", "Dela Cruz", "Aquino" }),
        ["so"] = (new[] { "Abdi", "Mohamed", "Ahmed", "Hassan", "Abdullahi", "Yusuf" },
                  new[] { "Hodan", "Amina", "Fadumo", "Sahra", "Ayaan", "Hibo" },
                  new[] { "Abdi", "Warsame", "Farah", "Hirsi", "Jama", "Osman", "Mohamud", "Ali" }),
        ["ja"] = (new[] { "Hiroshi", "Takashi", "Kenji", "Yuki", "Haruto", "Sota" },
                  new[] { "Yuko", "Keiko", "Aiko", "Yui", "Sakura", "Hina" },
                  new[] { "Sato", "Suzuki", "Takahashi", "Tanaka", "Watanabe", "Ito", "Yamamoto", "Nakamura" }),
    };

    static T Pick<T>(Random r, T[] a) => a[r.Next(a.Length)];

    public static int Generation(int birthYear) => birthYear < 1965 ? 0 : birthYear < 1995 ? 1 : 2;

    public static string SwissFirst(Random r, Lang l, bool male, int birthYear) =>
        Pick(r, (male ? SwissMale : SwissFemale)[(int)l][Generation(birthYear)]);

    public static string SwissSurname(Random r, Lang l) => Pick(r, SwissSurnames[(int)l]);

    public static string First(Random r, Country c, bool male, int birthYear)
    {
        var p = Pools.TryGetValue(c.Pool, out var v) ? v : Pools["en"];
        return Pick(r, male ? p.m : p.f);
    }

    public static string Surname(Random r, Country c) => Pick(r, (Pools.TryGetValue(c.Pool, out var v) ? v : Pools["en"]).s);

    /// <summary>Enfant né en Suisse de parents étrangers : prénom de la région ou du pays, au hasard.</summary>
    public static string ChildFirst(Random r, Country c, Lang region, bool male, int birthYear) =>
        r.NextDouble() < 0.35 ? SwissFirst(r, region, male, birthYear) : First(r, c, male, birthYear);

    /// <summary>Pays tiré selon les poids (groupe 1, 2 ou 3), corrigés par la région linguistique.</summary>
    public static Country PickCountry(Random r, int group, Lang region)
    {
        double total = 0;
        foreach (var c in Countries) if (c.Group == group) total += c.Weight * RegionFactor(c, region);
        double x = r.NextDouble() * total;
        foreach (var c in Countries)
        {
            if (c.Group != group) continue;
            x -= c.Weight * RegionFactor(c, region);
            if (x <= 0) return c;
        }
        return Countries[0];
    }
}

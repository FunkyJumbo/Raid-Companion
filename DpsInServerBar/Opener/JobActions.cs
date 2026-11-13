using System.Collections.Generic;

namespace DpsInServerBar.Opener;

/// <summary>
/// The actions of each job, in level order, then its role actions: the job guide of the official site
/// (na.finalfantasyxiv.com/jobguide), level 100. English names, matched to the game data at run time.
/// </summary>
public static class JobActions
{
    public static readonly IReadOnlyDictionary<string, string[]> ByJob = new Dictionary<string, string[]>
    {
        ["PLD"] =
        [
            "Fast Blade", "Fight or Flight", "Riot Blade", "Total Eclipse", "Shield Bash", "Iron Will",
            "Release Iron Will", "Shield Lob", "Rage of Halone", "Spirits Within", "Sheltron", "Sentinel",
            "Prominence", "Cover", "Circle of Scorn", "Hallowed Ground", "Bulwark", "Goring Blade", "Divine Veil",
            "Clemency", "Royal Authority", "Intervention", "Holy Spirit", "Intervene", "Requiescat",
            "Passage of Arms", "Holy Circle", "Atonement", "Supplication", "Sepulchre", "Confiteor", "Holy Sheltron",
            "Expiacion", "Blade of Faith", "Blade of Truth", "Blade of Valor", "Guardian", "Imperator",
            "Blade of Honor", "Rampart", "Low Blow", "Provoke", "Interject", "Reprisal", "Arm's Length", "Shirk",
        ],
        ["WAR"] =
        [
            "Heavy Swing", "Maim", "Berserk", "Overpower", "Defiance", "Release Defiance", "Tomahawk",
            "Storm's Path", "Thrill of Battle", "Inner Beast", "Vengeance", "Mythril Tempest", "Holmgang",
            "Steel Cyclone", "Storm's Eye", "Infuriate", "Fell Cleave", "Raw Intuition", "Equilibrium", "Decimate",
            "Onslaught", "Upheaval", "Shake It Off", "Inner Release", "Chaotic Cyclone", "Nascent Flash",
            "Inner Chaos", "Bloodwhetting", "Orogeny", "Primal Rend", "Damnation", "Primal Wrath",
            "Primal Ruination", "Rampart", "Low Blow", "Provoke", "Interject", "Reprisal", "Arm's Length", "Shirk",
        ],
        ["DRK"] =
        [
            "Hard Slash", "Syphon Strike", "Unleash", "Grit", "Release Grit", "Unmend", "Souleater",
            "Flood of Darkness", "Blood Weapon", "Shadow Wall", "Stalwart Soul", "Edge of Darkness", "Dark Mind",
            "Living Dead", "Salted Earth", "Shadowstride", "Abyssal Drain", "Carve and Spit", "Bloodspiller",
            "Quietus", "Dark Missionary", "Delirium", "The Blackest Night", "Flood of Shadow", "Edge of Shadow",
            "Living Shadow", "Oblation", "Salt and Darkness", "Shadowbringer", "Shadowed Vigil", "Scarlet Delirium",
            "Comeuppance", "Torcleaver", "Impalement", "Disesteem", "Rampart", "Low Blow", "Provoke", "Interject",
            "Reprisal", "Arm's Length", "Shirk",
        ],
        ["GNB"] =
        [
            "Keen Edge", "No Mercy", "Brutal Shell", "Camouflage", "Demon Slice", "Royal Guard",
            "Release Royal Guard", "Lightning Shot", "Danger Zone", "Solid Barrel", "Burst Strike", "Nebula",
            "Demon Slaughter", "Aurora", "Superbolide", "Sonic Break", "Trajectory", "Gnashing Fang", "Savage Claw",
            "Wicked Talon", "Bow Shock", "Heart of Light", "Heart of Stone", "Continuation", "Jugular Rip",
            "Abdomen Tear", "Eye Gouge", "Fated Circle", "Bloodfest", "Blasting Zone", "Heart of Corundum",
            "Hypervelocity", "Double Down", "Great Nebula", "Fated Brand", "Reign of Beasts", "Noble Blood",
            "Lion Heart", "Rampart", "Low Blow", "Provoke", "Interject", "Reprisal", "Arm's Length", "Shirk",
        ],
        ["WHM"] =
        [
            "Stone", "Cure", "Aero", "Medica", "Raise", "Stone II", "Cure II", "Presence of Mind", "Regen",
            "Cure III", "Aetherial Shift", "Holy", "Aero II", "Medica II", "Benediction", "Afflatus Solace",
            "Asylum", "Stone III", "Assize", "Thin Air", "Tetragrammaton", "Stone IV", "Divine Benison",
            "Plenary Indulgence", "Dia", "Glare", "Afflatus Misery", "Afflatus Rapture", "Temperance", "Glare III",
            "Holy III", "Aquaveil", "Liturgy of the Bell", "Glare IV", "Medica III", "Divine Caress", "Repose",
            "Esuna", "Lucid Dreaming", "Swiftcast", "Surecast", "Rescue",
        ],
        ["SCH"] =
        [
            "Ruin", "Bio", "Physick", "Summon Eos", "Resurrection", "Whispering Dawn", "Bio II", "Adloquium",
            "Succor", "Ruin II", "Fey Illumination", "Aetherflow", "Energy Drain", "Lustrate", "Art of War",
            "Sacred Soil", "Indomitability", "Broil", "Deployment Tactics", "Emergency Tactics", "Dissipation",
            "Excogitation", "Broil II", "Chain Stratagem", "Aetherpact", "Dissolve Union", "Biolysis", "Broil III",
            "Recitation", "Fey Blessing", "Summon Seraph", "Consolation", "Broil IV", "Art of War II", "Protraction",
            "Expedient", "Baneful Impaction", "Concitation", "Seraphism", "Manifestation", "Accession", "Repose",
            "Esuna", "Lucid Dreaming", "Swiftcast", "Surecast", "Rescue",
        ],
        ["AST"] =
        [
            "Malefic", "Benefic", "Combust", "Lightspeed", "Helios", "Ascend", "Essential Dignity", "Benefic II",
            "Astral Draw", "Umbral Draw", "Play I", "Play II", "Play III", "Aspected Benefic", "Aspected Helios",
            "Gravity", "Combust II", "Synastry", "Divination", "Malefic II", "Collective Unconscious",
            "Celestial Opposition", "Earthly Star", "Stellar Detonation", "Malefic III", "Minor Arcana",
            "Combust III", "Malefic IV", "Celestial Intersection", "Horoscope", "Neutral Sect", "Fall Malefic",
            "Gravity II", "Exaltation", "Macrocosmos", "Microcosmos", "Oracle", "Helios Conjunction", "Sun Sign",
            "Repose", "Esuna", "Lucid Dreaming", "Swiftcast", "Surecast", "Rescue",
        ],
        ["SGE"] =
        [
            "Dosis", "Diagnosis", "Kardia", "Prognosis", "Egeiro", "Physis", "Phlegma", "Eukrasia",
            "Eukrasian Diagnosis", "Eukrasian Prognosis", "Eukrasian Dosis", "Soteria", "Icarus", "Druochole",
            "Dyskrasia", "Kerachole", "Ixochole", "Zoe", "Pepsis", "Physis II", "Taurochole", "Toxikon", "Haima",
            "Dosis II", "Phlegma II", "Eukrasian Dosis II", "Rhizomata", "Holos", "Panhaima", "Dosis III",
            "Phlegma III", "Eukrasian Dosis III", "Dyskrasia II", "Eukrasian Dyskrasia", "Toxikon II", "Krasis",
            "Pneuma", "Psyche", "Eukrasian Prognosis II", "Philosophia", "Repose", "Esuna", "Lucid Dreaming",
            "Swiftcast", "Surecast", "Rescue",
        ],
        ["MNK"] =
        [
            "Bootshine", "True Strike", "Snap Punch", "Steeled Meditation", "Steel Peak", "Twin Snakes",
            "Arm of the Destroyer", "Demolish", "Rockbreaker", "Thunderclap", "Inspirited Meditation",
            "Howling Fist", "Mantra", "Four-point Fury", "Dragon Kick", "Perfect Balance", "Form Shift",
            "Forbidden Meditation", "The Forbidden Chakra", "Masterful Blitz", "Tornado Kick", "Elixir Field",
            "Celestial Revolution", "Flint Strike", "Riddle of Earth", "Earth's Reply", "Riddle of Fire",
            "Brotherhood", "Riddle of Wind", "Enlightened Meditation", "Enlightenment", "Six-sided Star",
            "Shadow of the Destroyer", "Rising Phoenix", "Phantom Rush", "Leaping Opo", "Rising Raptor",
            "Pouncing Coeurl", "Elixir Burst", "Wind's Reply", "Fire's Reply", "Second Wind", "Leg Sweep",
            "Bloodbath", "Feint", "Arm's Length", "True North",
        ],
        ["DRG"] =
        [
            "True Thrust", "Vorpal Thrust", "Life Surge", "Piercing Talon", "Disembowel", "Full Thrust",
            "Lance Charge", "Jump", "Elusive Jump", "Doom Spike", "Winged Glide", "Chaos Thrust", "Dragonfire Dive",
            "Battle Litany", "Fang and Claw", "Wheeling Thrust", "Geirskogul", "Sonic Thrust", "Drakesbane",
            "Mirage Dive", "Nastrond", "Coerthan Torment", "High Jump", "Raiden Thrust", "Stardiver",
            "Draconian Fury", "Heavens' Thrust", "Chaotic Spring", "Wyrmwind Thrust", "Rise of the Dragon",
            "Lance Barrage", "Spiral Blow", "Starcross", "Second Wind", "Leg Sweep", "Bloodbath", "Feint",
            "Arm's Length", "True North",
        ],
        ["NIN"] =
        [
            "Spinning Edge", "Shade Shift", "Gust Slash", "Hide", "Throwing Dagger", "Mug", "Trick Attack",
            "Aeolian Edge", "Ten", "Ninjutsu", "Chi", "Death Blossom", "Assassinate", "Shukuchi", "Jin", "Kassatsu",
            "Hakke Mujinsatsu", "Armor Crush", "Dream Within a Dream", "Hellfrog Medium", "Dokumori", "Bhavacakra",
            "Ten Chi Jin", "Meisui", "Bunshin", "Phantom Kamaitachi", "Hollow Nozuchi", "Forked Raiju",
            "Fleeting Raiju", "Kunai's Bane", "Deathfrog Medium", "Zesho Meppo", "Tenri Jindo", "Second Wind",
            "Leg Sweep", "Bloodbath", "Feint", "Arm's Length", "True North",
        ],
        ["SAM"] =
        [
            "Hakaze", "Jinpu", "Third Eye", "Enpi", "Shifu", "Fuga", "Gekko", "Iaijutsu", "Mangetsu", "Kasha", "Oka",
            "Yukikaze", "Meikyo Shisui", "Hissatsu: Shinten", "Hissatsu: Gyoten", "Hissatsu: Yaten", "Meditate",
            "Hissatsu: Kyuten", "Hagakure", "Ikishoten", "Hissatsu: Guren", "Hissatsu: Senei", "Tsubame-gaeshi",
            "Shoha", "Tengentsu", "Fuko", "Ogi Namikiri", "Kaeshi: Namikiri", "Gyofu", "Zanshin", "Second Wind",
            "Leg Sweep", "Bloodbath", "Feint", "Arm's Length", "True North",
        ],
        ["RPR"] =
        [
            "Slice", "Waxing Slice", "Shadow of Death", "Harpe", "Hell's Ingress", "Hell's Egress",
            "Spinning Scythe", "Infernal Slice", "Whorl of Death", "Arcane Crest", "Nightmare Scythe", "Blood Stalk",
            "Grim Swathe", "Soul Slice", "Soul Scythe", "Gibbet", "Gallows", "Guillotine", "Unveiled Gibbet",
            "Unveiled Gallows", "Arcane Circle", "Regress", "Gluttony", "Enshroud", "Void Reaping", "Cross Reaping",
            "Grim Reaping", "Soulsow", "Harvest Moon", "Lemure's Slice", "Lemure's Scythe", "Plentiful Harvest",
            "Communio", "Sacrificium", "Executioner's Gibbet", "Executioner's Gallows", "Executioner's Guillotine",
            "Perfectio", "Second Wind", "Leg Sweep", "Bloodbath", "Feint", "Arm's Length", "True North",
        ],
        ["VPR"] =
        [
            "Steel Fangs", "Hunter's Sting", "Reaving Fangs", "Writhing Snap", "Swiftskin's Sting", "Steel Maw",
            "Flanksting Strike", "Flanksbane Fang", "Hindsting Strike", "Hindsbane Fang", "Reaving Maw", "Slither",
            "Hunter's Bite", "Swiftskin's Bite", "Jagged Maw", "Bloodied Maw", "Serpent's Tail", "Death Rattle",
            "Last Lash", "Vicewinder", "Hunter's Coil", "Swiftskin's Coil", "Vicepit", "Hunter's Den",
            "Swiftskin's Den", "Twinfang", "Twinblood", "Twinfang Bite", "Twinblood Bite", "Twinfang Thresh",
            "Twinblood Thresh", "Uncoiled Fury", "Serpent's Ire", "Reawaken", "First Generation",
            "Second Generation", "Third Generation", "Fourth Generation", "Uncoiled Twinfang", "Uncoiled Twinblood",
            "Ouroboros", "First Legacy", "Second Legacy", "Third Legacy", "Fourth Legacy", "Second Wind",
            "Leg Sweep", "Bloodbath", "Feint", "Arm's Length", "True North",
        ],
        ["BRD"] =
        [
            "Heavy Shot", "Straight Shot", "Raging Strikes", "Venomous Bite", "Bloodletter", "Repelling Shot",
            "Quick Nock", "Wide Volley", "Windbite", "Mage's Ballad", "The Warden's Paean", "Barrage",
            "Army's Paeon", "Rain of Death", "Battle Voice", "The Wanderer's Minuet", "Pitch Perfect",
            "Empyreal Arrow", "Iron Jaws", "Sidewinder", "Troubadour", "Caustic Bite", "Stormbite", "Nature's Minne",
            "Refulgent Arrow", "Shadowbite", "Burst Shot", "Apex Arrow", "Ladonsbite", "Blast Arrow",
            "Radiant Finale", "Heartbreak Shot", "Resonant Arrow", "Radiant Encore", "Leg Graze", "Second Wind",
            "Foot Graze", "Peloton", "Head Graze", "Arm's Length",
        ],
        ["MCH"] =
        [
            "Split Shot", "Slug Shot", "Hot Shot", "Reassemble", "Gauss Round", "Spread Shot", "Clean Shot",
            "Hypercharge", "Heat Blast", "Rook Autoturret", "Rook Overdrive", "Rook Overload", "Wildfire",
            "Detonator", "Ricochet", "Auto Crossbow", "Heated Split Shot", "Tactician", "Drill", "Heated Slug Shot",
            "Dismantle", "Heated Clean Shot", "Barrel Stabilizer", "Blazing Shot", "Flamethrower", "Bioblaster",
            "Air Anchor", "Automaton Queen", "Queen Overdrive", "Arm Punch", "Roller Dash", "Pile Bunker",
            "Scattergun", "Crowned Collider", "Chain Saw", "Double Check", "Checkmate", "Excavator",
            "Full Metal Field", "Leg Graze", "Second Wind", "Foot Graze", "Peloton", "Head Graze", "Arm's Length",
        ],
        ["DNC"] =
        [
            "Cascade", "Fountain", "Windmill", "Standard Step", "Standard Finish", "Reverse Cascade", "Bladeshower",
            "Fan Dance", "Rising Windmill", "Fountainfall", "Bloodshower", "Fan Dance II", "En Avant",
            "Curing Waltz", "Shield Samba", "Closed Position", "Ending", "Devilment", "Fan Dance III",
            "Technical Step", "Technical Finish", "Flourish", "Saber Dance", "Improvisation", "Improvised Finish",
            "Tillana", "Fan Dance IV", "Starfall Dance", "Last Dance", "Finishing Move", "Dance of the Dawn",
            "Leg Graze", "Second Wind", "Foot Graze", "Peloton", "Head Graze", "Arm's Length",
        ],
        ["BLM"] =
        [
            "Blizzard", "Fire", "Transpose", "Thunder", "Blizzard II", "Scathe", "Fire II", "Thunder II", "Manaward",
            "Manafont", "Fire III", "Blizzard III", "Umbral Soul", "Freeze", "Thunder III", "Aetherial Manipulation",
            "Flare", "Ley Lines", "Blizzard IV", "Fire IV", "Between the Lines", "Thunder IV", "Triplecast", "Foul",
            "Despair", "Xenoglossy", "High Fire II", "High Blizzard II", "Amplifier", "Paradox", "High Thunder",
            "High Thunder II", "Retrace", "Flare Star", "Addle", "Sleep", "Lucid Dreaming", "Swiftcast", "Surecast",
        ],
        ["SMN"] =
        [
            "Ruin", "Summon Carbuncle", "Radiant Aegis", "Physick", "Aethercharge", "Summon Ruby", "Gemshine",
            "Ruby Ruin", "Fester", "Energy Drain", "Resurrection", "Summon Topaz", "Topaz Ruin", "Summon Emerald",
            "Emerald Ruin", "Outburst", "Precious Brilliance", "Ruby Outburst", "Topaz Outburst", "Emerald Outburst",
            "Ruin II", "Summon Ifrit", "Ruby Ruin II", "Topaz Ruin II", "Emerald Ruin II", "Summon Titan",
            "Painflare", "Summon Garuda", "Energy Siphon", "Ruin III", "Ruby Ruin III", "Topaz Ruin III",
            "Emerald Ruin III", "Dreadwyrm Trance", "Astral Impulse", "Astral Flare", "Astral Flow", "Deathflare",
            "Ruin IV", "Searing Light", "Summon Bahamut", "Wyrmwave", "Enkindle Bahamut", "Akh Morn", "Ruby Rite",
            "Topaz Rite", "Emerald Rite", "Tri-disaster", "Ruby Disaster", "Topaz Disaster", "Emerald Disaster",
            "Summon Phoenix", "Fountain of Fire", "Brand of Purgatory", "Rekindle", "Enkindle Phoenix",
            "Everlasting Flight", "Scarlet Flame", "Revelation", "Ruby Catastrophe", "Topaz Catastrophe",
            "Emerald Catastrophe", "Crimson Cyclone", "Crimson Strike", "Mountain Buster", "Slipstream",
            "Summon Ifrit II", "Summon Titan II", "Summon Garuda II", "Necrotize", "Searing Flash", "Lux Solaris",
            "Summon Solar Bahamut", "Umbral Impulse", "Umbral Flare", "Sunflare", "Enkindle Solar Bahamut",
            "Luxwave", "Exodus", "Addle", "Sleep", "Lucid Dreaming", "Swiftcast", "Surecast",
        ],
        ["RDM"] =
        [
            "Riposte", "Jolt", "Verthunder", "Corps-a-corps", "Veraero", "Scatter", "Verthunder II", "Veraero II",
            "Verfire", "Verstone", "Zwerchhau", "Displacement", "Engagement", "Fleche", "Redoublement",
            "Acceleration", "Moulinet", "Vercure", "Contre Sixte", "Embolden", "Manafication", "Jolt II", "Verraise",
            "Impact", "Verflare", "Verholy", "Reprise", "Scorch", "Verthunder III", "Veraero III", "Jolt III",
            "Magick Barrier", "Resolution", "Vice of Thorns", "Grand Impact", "Prefulgence", "Enchanted Riposte",
            "Enchanted Zwerchhau", "Enchanted Redoublement", "Enchanted Moulinet", "Enchanted Moulinet Deux",
            "Enchanted Moulinet Trois", "Enchanted Reprise", "Addle", "Sleep", "Lucid Dreaming", "Swiftcast",
            "Surecast",
        ],
        ["PCT"] =
        [
            "Fire in Red", "Aero in Green", "Tempera Coat", "Water in Blue", "Smudge", "Fire II in Red",
            "Creature Motif", "Living Muse", "Mog of the Ages", "Pom Motif", "Wing Motif", "Pom Muse", "Winged Muse",
            "Aero II in Green", "Water II in Blue", "Weapon Motif", "Steel Muse", "Hammer Stamp", "Hammer Motif",
            "Striking Muse", "Blizzard in Cyan", "Blizzard II in Cyan", "Subtractive Palette", "Stone in Yellow",
            "Thunder in Magenta", "Stone II in Yellow", "Thunder II in Magenta", "Landscape Motif", "Scenic Muse",
            "Starry Sky Motif", "Starry Muse", "Holy in White", "Hammer Brush", "Polishing Hammer", "Tempera Grassa",
            "Comet in Black", "Rainbow Drip", "Claw Motif", "Maw Motif", "Clawed Muse", "Fanged Muse",
            "Retribution of the Madeen", "Star Prism", "Addle", "Sleep", "Lucid Dreaming", "Swiftcast", "Surecast",
        ],
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FhirMpi.Library.Helpers
{
    public static class Constants
    {
        public static List<string> Colours = new List<string>
        {
            "White",
            "Yellow",
            "Blue",
            "Red",
            "Green",
            "Black",
            "Brown",
            "Azure",
            //"Ivory",
            //"Teal",
            //"Silver",
            //"Purple",
            //"Navy blue",
            //"Pea green",
            //"Gray",
            //"Orange",
            //"Maroon",
            //"Charcoal",
            //"Aquamarine",
            //"Coral",
            //"Fuchsia",
            //"Wheat",
            //"Lime",
            //"Crimson",
            //"Khaki",
            //"Magenta",
            //"Olden",
            //"Plum",
            //"Olive",
            //"Cyan"
        };
        public static List<string> Animals = new List<string>
        {
            "Alligator",
            "Ant",
            "Bear",
            "Bee",
            "Bird",
            "Camel",
            //"Cat",
            //"Cheetah",
            //"Chicken",
            //"Chimpanzee",
            //"Cow",
            //"Crocodile",
            //"Deer",
            //"Dog",
            //"Dolphin",
            //"Duck",
            //"Eagle",
            //"Elephant",
            //"Fish",
            //"Fox",
            //"Frog",
            //"Giraffe",
            //"Goat",
            //"Goldfish",
            //"Hamster",
            //"Hippopotamus",
            //"Horse",
            //"Kangaroo",
            //"Kitten",
            //"Lion",
            //"Lobster",
            //"Monkey",
            //"Octopus",
            //"Owl",
            //"Panda",
            //"Pig",
            //"Puppy",
            //"Rat",
            //"Scorpion",
            //"Seal",
            //"Shark",
            //"Sheep",
            //"Snail",
            //"Snake",
            //"Spider",
            //"Squirrel",
            //"Tiger",
            //"Turtle",
            //"Wolf",
            //"Zebra"
        };
        public static List<string> StreetNames = new List<string>
        {
            "High Street",
            "Station Road",
            "Main Street",
            "Park Road",
            "Church Road",
            "Church Street",
            "London Road",
            //"Victoria Road",
            //"Green Lane",
            //"Manor Road",
            //"Church Lane",
            //"Park Avenue",
            //"The Avenue",
            //"The Crescent",
            //"Queens Road",
            //"New Road",
            //"Grange Road",
            //"Kings Road",
            //"Kingsway",
            //"Windsor Road",
            //"Highfield Road",
            //"Mill Lane",
            //"Alexander Road",
            //"York Road",
            //"St. John’s Road",
            //"Main Road",
            //"Broadway",
            //"King Street",
            //"The Green",
            //"Springfield Road",
            //"George Street",
            //"Park Lane",
            //"Victoria Street",
            //"Albert Road",
            //"Queensway",
            //"New Street",
            //"Queen Street",
            //"West Street",
            //"North Street",
            //"Manchester Road",
            //"The Grove",
            //"Richmond Road",
            //"Grove Road",
            //"South Street",
            //"School Lane",
            //"The Drive",
            //"North Road",
            //"Stanley Road",
            //"Chester Road",
            //"Mill Road"
        };
        public static List<string> Cities = new List<string>
        {
            "Bangor",
            "Cardiff",
            "Newport",
            "St Davids",
            "Swansea",
        };

        public static readonly char[] CHARS_ALPHA;
        public static readonly char[] CHARS_ALPHANUM;
        public static readonly char[] CHARS_ENGLISH_ALPHANUM;
        public static readonly char[] CHARS_ENGLISH_ALPHA;
        public static readonly char[] CHARS_ENGLISH_ALPHA_WHITESPACE;
        public static readonly char[] CHARS_ENGLISH_ALPHANUM_WHITESPACE;
        public static readonly char[] CHARS_ENGLISH_PRINTABLE;
        public static readonly char[] CHARS_ENGLISH_PRINTABLE_WHITESPACE;
        public static readonly char[] CHARS_PRINTABLE;
        public static readonly char[] CHARS_PRINTABLE_WHITESPACE;
        public static readonly char[] CHARS_ALPHA_UPPER = new[]
                                                         {
                                                             'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H',
                                                             'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P',
                                                             'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X',
                                                             'Y', 'Z'
                                                         };
        public static readonly char[] CHARS_ALPHA_LOWER = new[]
                                                       {
                                                             'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h',
                                                             'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p',
                                                             'q', 'r', 's', 't', 'u', 'v', 'w', 'x',
                                                             'y', 'z'
                                                         };
        public static readonly char[] CHARS_CHINESE = new[]        // Traditional(?) Chinese
                                                         {
                                                             '健', '康','繁','荣'
                                                         };
        public static readonly char[] CHARS_CYRILLIC = new[]       // Cyrillic
                                                         {
                                                             'И', 'в', 'а', 'н', 'Ч', 'е', 'т', 'ё', 'р', 'ы', 'й', 'с', 'и', 'л', 'ь', 'ч'
                                                         };
        public static readonly char[] CHARS_NUM = new[]
                                                       {
                                                           '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'
                                                       };

        public static readonly char[] CHARS_SPECIAL1 = new[]
                                                            {
                                                                // Specifically excludes '@', '[', ']', '\', and '.'
                                                                '!', '#', '$', '%', '&', '\'', '*', '+',
                                                                '-', '/' , '=', '?', '^', '_', '`', '{',
                                                                '|', '}'
                                                            };
        public static readonly char[] CHARS_SPECIAL2 = new[]
                                                            {
                                                                '@', '[', ']', '\\', '.'
                                                            };
        public static readonly char[] CHARS_WHITESPACE = new[]
                                                            {
                                                                ' ', '\t'
                                                                // but not '\r', '\n',
                                                            };

        static Constants()
        {
            var tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_CHINESE);
            tempCharset.AddRange(CHARS_CYRILLIC);
            CHARS_ALPHA = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_CHINESE);
            tempCharset.AddRange(CHARS_CYRILLIC);
            tempCharset.AddRange(CHARS_NUM);
            CHARS_ALPHANUM = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            CHARS_ENGLISH_ALPHA = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_WHITESPACE);
            CHARS_ENGLISH_ALPHA_WHITESPACE = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_NUM);
            CHARS_ENGLISH_ALPHANUM = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_NUM);
            tempCharset.AddRange(CHARS_WHITESPACE);
            CHARS_ENGLISH_ALPHANUM_WHITESPACE = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_NUM);
            tempCharset.AddRange(CHARS_SPECIAL1);
            CHARS_ENGLISH_PRINTABLE = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_NUM);
            tempCharset.AddRange(CHARS_SPECIAL1);
            tempCharset.AddRange(CHARS_WHITESPACE);
            CHARS_ENGLISH_PRINTABLE_WHITESPACE = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_CHINESE);
            tempCharset.AddRange(CHARS_CYRILLIC);
            tempCharset.AddRange(CHARS_NUM);
            tempCharset.AddRange(CHARS_SPECIAL1);
            CHARS_PRINTABLE = tempCharset.ToArray();

            tempCharset = new List<char>();
            tempCharset.AddRange(CHARS_ALPHA_UPPER);
            tempCharset.AddRange(CHARS_ALPHA_LOWER);
            tempCharset.AddRange(CHARS_CHINESE);
            tempCharset.AddRange(CHARS_CYRILLIC);
            tempCharset.AddRange(CHARS_NUM);
            tempCharset.AddRange(CHARS_SPECIAL1);
            tempCharset.AddRange(CHARS_WHITESPACE);
            CHARS_PRINTABLE_WHITESPACE = tempCharset.ToArray();
        }
    }
}
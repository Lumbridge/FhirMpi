using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Support;

namespace FhirMpi.Library.Helpers
{
    public static class RandomHelper
    {
        private static readonly Random Random = new Random();

        public static string NextString(this Random random, char[] validChars, int minLength, int maxLength)
        {
            if (minLength > maxLength) {
                return "";
            }
            var result = "";
            var length = GetRandomInteger(minLength, maxLength);
            for (var i = 0; i < length; i++)
            {
                result += random.NextChoice(validChars);
            }
            if (result.Length > maxLength)
            {
                throw new Exception($"Illegal value produced. maxLength={maxLength}; result.Length={result.Length}; value={result}");
            }
            return result;
        }

        public static T NextChoice<T>(this Random random, IList<T> collection)
        {
            if (collection == null || collection.Count == 0)
            {
                throw new Exception("Collection was null or empty.");
            }
            var item = collection[GetRandomInteger(0, collection.Count - 1)];
            return item;
        }

        public static int GetRandomInteger(int min, int max)
        {
            lock (Random)
            {
                return Random.Next(min, max + 1);
            }
        }

        public static string GetRandomNumberOfFixedLength(int length)
        {
            var s = string.Empty;
            for (var i = 0; i < length; i++)
                s = string.Concat(s, GetRandomInteger(0, 9).ToString());
            return s;
        }

        public static string GetRandomPostCode()
        {
            var postCode = Random.NextString(Constants.CHARS_ALPHA_UPPER, 2, 2);
            postCode += Random.NextString(Constants.CHARS_NUM, 1, 2);
            postCode += " ";
            postCode += Random.NextString(Constants.CHARS_NUM, 1, 1);
            postCode += Random.NextString(Constants.CHARS_ALPHA_UPPER, 2, 2);
            return postCode;
        }

        public static AdministrativeGender GetRandomFhirGender()
        {
            return (AdministrativeGender)GetRandomInteger(0, 3);
        }

        public static DateTime GetRandomDate(DateTime min, DateTime max)
        {
            var range = (max - min).Days;
            lock (Random)
            {
                return min.AddDays(Random.Next(range));
            }
        }

        public static FhirDateTime GetRandomFhirDate(DateTime min, DateTime max)
        {
            var range = (max - min).Days;
            lock (Random)
            {
                return new FhirDateTime(min.AddDays(Random.Next(range)).ToFhirDate());
            }
        }

        public static HumanName GetRandomHumanName()
        {
            var givenName = Random.NextChoice(Constants.Colours);
            var familyName = Random.NextChoice(Constants.Animals);
            return new HumanName().WithGiven(givenName).AndFamily(familyName);
        }

        public static Address GetRandomFhirAddress()
        {
            var address = new Address
            {
                Line = new List<string>
                {
                    Random.NextChoice(Constants.StreetNames)
                },
                City = Random.NextChoice(Constants.Cities),
                Country = "Wales",
                PostalCode = GetRandomPostCode()
            };
            return address;
        }

        public static Patient GetRandomFhirPatient()
        {
            return new Patient
            {
                Identifier = new List<Identifier>
                {
                    new Identifier("NHS", GetRandomNumberOfFixedLength(10))
                },
                Name = new List<HumanName>
                {
                    GetRandomHumanName()
                },
                BirthDate = GetRandomFhirDate(new DateTime(1985, 1, 1), DateTime.Now).ToString(),
                Address = new List<Address>
                {
                    GetRandomFhirAddress()
                },
                Gender = GetRandomFhirGender()
            };
        }
    }
}

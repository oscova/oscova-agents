using System.ComponentModel;

namespace Oscova.Agents.Tests.Framework.TestTools
{
    // Has ~45 tools covering various functionalities
    public class LargeToolSet
    {
        // Mathematical Operations (1-10)

        [DisplayName("calculate-square")]
        public static double CalculateSquare(double number)
        {
            return number * number;
        }

        public static double CalculateCube(double number)
        {
            return number * number * number;
        }

        [DisplayName("sqrt")]
        public static double SquareRoot(double number)
        {
            return Math.Sqrt(number);
        }

        [DisplayName("power-calculation")]
        public static double Power(double baseNumber, double exponent)
        {
            return Math.Pow(baseNumber, exponent);
        }

        public static double AbsoluteValue(double number)
        {
            return Math.Abs(number);
        }

        public static double Modulo(double dividend, double divisor)
        {
            return dividend % divisor;
        }

        [DisplayName("floor")]
        public static double FloorValue(double number)
        {
            return Math.Floor(number);
        }

        public static double CeilingValue(double number)
        {
            return Math.Ceiling(number);
        }

        [DisplayName("round-number")]
        public static double RoundNumber(double number)
        {
            return Math.Round(number);
        }

        public static double CalculatePercentage(double value, double percentage)
        {
            return (value * percentage) / 100;
        }

        // String Operations (11-20)

        [DisplayName("string-length")]
        public static int GetStringLength(string text)
        {
            return text.Length;
        }

        public static string ToUpperCase(string text)
        {
            return text.ToUpper();
        }

        [DisplayName("lowercase")]
        public static string ToLowerCase(string text)
        {
            return text.ToLower();
        }

        [DisplayName("reverse-string")]
        public static string ReverseString(string text)
        {
            char[] charArray = text.ToCharArray();
            Array.Reverse(charArray);
            return new string(charArray);
        }

        public static int CountVowels(string text)
        {
            return text.Count(c => "aeiouAEIOU".Contains(c));
        }

        public static int CountConsonants(string text)
        {
            return text.Count(c => char.IsLetter(c) && !"aeiouAEIOU".Contains(c));
        }

        [DisplayName("check-palindrome")]
        public static bool IsPalindrome(string text)
        {
            string cleaned = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLower();
            return cleaned.SequenceEqual(cleaned.Reverse());
        }

        [DisplayName("join-strings")]
        public static string JoinStrings(string first, string second, string separator = " ")
        {
            return $"{first}{separator}{second}";
        }

        public static string RepeatString(string text, int times)
        {
            return string.Concat(Enumerable.Repeat(text, times));
        }

        public static string ExtractSubstring(string text, int start, int length)
        {
            return text.Substring(start, Math.Min(length, text.Length - start));
        }

        // Date and Time Operations (21-30)

        [DisplayName("get-current-datetime")]
        public static string GetCurrentDateTime()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public static string GetDayOfWeek()
        {
            return DateTime.Now.DayOfWeek.ToString();
        }

        [DisplayName("add-days")]
        public static string AddDaysToToday(int days)
        {
            return DateTime.Now.AddDays(days).ToString("yyyy-MM-dd");
        }

        public static int GetCurrentYear()
        {
            return DateTime.Now.Year;
        }

        [DisplayName("current-month-name")]
        public static string GetCurrentMonthName()
        {
            return DateTime.Now.ToString("MMMM");
        }

        public static int CalculateAge(int birthYear)
        {
            return DateTime.Now.Year - birthYear;
        }

        [DisplayName("is-leap-year")]
        public static bool IsLeapYear(int year)
        {
            return DateTime.IsLeapYear(year);
        }

        public static int DaysUntilEndOfYear()
        {
            var endOfYear = new DateTime(DateTime.Now.Year, 12, 31);
            return (endOfYear - DateTime.Now).Days;
        }

        [DisplayName("get-quarter")]
        public static int GetQuarterOfYear()
        {
            return (DateTime.Now.Month - 1) / 3 + 1;
        }

        public static long GetUnixTimestamp()
        {
            return DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        // Collection Operations (31-40)

        [DisplayName("sum-array")]
        public static double SumArray(double[] numbers)
        {
            return numbers.Sum();
        }

        public static double AverageArray(double[] numbers)
        {
            return numbers.Average();
        }

        [DisplayName("max")]
        public static double MaxValue(double[] numbers)
        {
            return numbers.Max();
        }

        public static double MinValue(double[] numbers)
        {
            return numbers.Min();
        }

        [DisplayName("count-elements")]
        public static int CountElements(double[] numbers)
        {
            return numbers.Length;
        }

        public static double FirstElement(double[] numbers)
        {
            return numbers[0];
        }

        [DisplayName("last")]
        public static double LastElement(double[] numbers)
        {
            return numbers[^1];
        }

        public static bool ArrayContains(double[] numbers, double value)
        {
            return numbers.Contains(value);
        }

        [DisplayName("get-distinct")]
        public static double[] GetDistinctValues(double[] numbers)
        {
            return numbers.Distinct().ToArray();
        }

        public static double[] SortArray(double[] numbers)
        {
            return numbers.Order().ToArray();
        }

        // Utility and Conversion Operations (41-50)

        [DisplayName("celsius-to-fahrenheit")]
        public static double CelsiusToFahrenheit(double celsius)
        {
            return (celsius * 9 / 5) + 32;
        }

        public static double FahrenheitToCelsius(double fahrenheit)
        {
            return (fahrenheit - 32) * 5 / 9;
        }

        [DisplayName("km-to-miles")]
        public static double KilometersToMiles(double kilometers)
        {
            return kilometers * 0.621371;
        }

        public static double MilesToKilometers(double miles)
        {
            return miles * 1.60934;
        }

        [DisplayName("generate-random")]
        public static int GenerateRandomNumber(int min, int max)
        {
            return Random.Shared.Next(min, max + 1);
        }

        public static bool IsEven(int number)
        {
            return number % 2 == 0;
        }

        [DisplayName("is-odd")]
        public static bool IsOdd(int number)
        {
            return number % 2 != 0;
        }

        public static bool IsPrime(int number)
        {
            if (number <= 1) return false;
            if (number == 2) return true;
            if (number % 2 == 0) return false;

            for (int i = 3; i <= Math.Sqrt(number); i += 2)
            {
                if (number % i == 0) return false;
            }
            return true;
        }

        [DisplayName("calculate-factorial")]
        public static long CalculateFactorial(int number)
        {
            if (number < 0) return 0;
            if (number == 0 || number == 1) return 1;

            long result = 1;
            for (int i = 2; i <= number; i++)
            {
                result *= i;
            }
            return result;
        }

        [Description("Generates Fibonacci sequence with specified number of terms.")]
        public static string GenerateFibonacci(int terms)
        {
            if (terms <= 0) return "[]";
            if (terms == 1) return "[0]";

            List<long> fibonacci = [0, 1];
            for (int i = 2; i < terms; i++)
            {
                fibonacci.Add(fibonacci[i - 1] + fibonacci[i - 2]);
            }

            return $"[{string.Join(", ", fibonacci.Take(terms))}]";
        }
    }
}

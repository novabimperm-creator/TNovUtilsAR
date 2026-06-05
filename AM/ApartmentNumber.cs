using System;

namespace TNovUtilsAR
{
    public sealed class ApartmentNumber
    {
        public int Section { get; }
        public string Floor { get; }
        public string Apartment { get; }
        public string Raw { get; }

        private ApartmentNumber(int section, string floor, string apartment, string raw)
        {
            Section = section;
            Floor = floor;
            Apartment = apartment;
            Raw = raw;
        }

        public string SectionPadded => Section.ToString("D2");

        public static ApartmentNumber Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new FormatException("Параметр T_Номер продаваемого помещения пустой.");

            var parts = raw.Trim().Split('-');
            if (parts.Length != 3)
                throw new FormatException($"Ожидался формат С-Э-Н, получено: '{raw}'.");

            if (!int.TryParse(parts[0], out var section))
                throw new FormatException($"Секция не число: '{parts[0]}' в '{raw}'.");

            return new ApartmentNumber(section, parts[1].Trim(), parts[2].Trim(), raw.Trim());
        }
    }
}

namespace Acme.QuoteApi;

public record DriverInput(string? FirstName, string? LastName, int Age, int YearsLicensed, int Incidents);
public record VehicleInput(int Year, string? Make, string? Model, int AnnualMiles);
public record QuoteRequest(DriverInput? Driver, VehicleInput? Vehicle, string? Coverage);
public record PriceLine(string Label, decimal Amount);
public record PriceResult(decimal Monthly, decimal Annual, IReadOnlyList<PriceLine> Breakdown);

/// <summary>
/// Demo pricing only. The numbers are invented to make the walkthrough
/// behave sensibly; they are not actuarial and not anyone's real rates.
/// </summary>
public static class Pricing
{
    public static readonly string[] Coverages = ["basic", "standard", "premium"];

    public static Dictionary<string, string[]> Validate(QuoteRequest req, int currentYear)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = [message];

        if (req.Driver is null) Add("driver", "Driver details are required.");
        else
        {
            if (string.IsNullOrWhiteSpace(req.Driver.FirstName)) Add("driver.firstName", "Enter a first name.");
            if (string.IsNullOrWhiteSpace(req.Driver.LastName)) Add("driver.lastName", "Enter a last name.");
            if (req.Driver.Age is < 16 or > 100) Add("driver.age", "Age must be between 16 and 100.");
            if (req.Driver.YearsLicensed < 0 || req.Driver.YearsLicensed > req.Driver.Age - 15)
                Add("driver.yearsLicensed", "Years licensed does not fit the driver's age.");
            if (req.Driver.Incidents is < 0 or > 10) Add("driver.incidents", "Incidents must be between 0 and 10.");
        }

        if (req.Vehicle is null) Add("vehicle", "Vehicle details are required.");
        else
        {
            if (req.Vehicle.Year < 1990 || req.Vehicle.Year > currentYear + 1)
                Add("vehicle.year", $"Model year must be between 1990 and {currentYear + 1}.");
            if (string.IsNullOrWhiteSpace(req.Vehicle.Make)) Add("vehicle.make", "Choose a make.");
            if (string.IsNullOrWhiteSpace(req.Vehicle.Model)) Add("vehicle.model", "Enter a model.");
            if (req.Vehicle.AnnualMiles is < 1000 or > 60000)
                Add("vehicle.annualMiles", "Annual miles must be between 1,000 and 60,000.");
        }

        if (req.Coverage is null || !Coverages.Contains(req.Coverage))
            Add("coverage", "Coverage must be basic, standard or premium.");

        return errors;
    }

    public static PriceResult Calculate(QuoteRequest req, int currentYear)
    {
        var driver = req.Driver!;
        var vehicle = req.Vehicle!;
        var lines = new List<PriceLine>();

        decimal annual = req.Coverage switch
        {
            "basic" => 780m,
            "premium" => 1680m,
            _ => 1140m,
        };
        lines.Add(new PriceLine($"Base rate, {req.Coverage} coverage", annual));

        void Apply(string label, decimal factor)
        {
            var delta = Math.Round(annual * factor, 2);
            if (delta == 0m) return;
            annual += delta;
            lines.Add(new PriceLine(label, delta));
        }

        if (driver.Age < 25) Apply("Driver under 25", 0.35m);
        else if (driver.Age >= 70) Apply("Driver 70 or over", 0.15m);

        if (driver.YearsLicensed < 3) Apply("Licensed for under 3 years", 0.20m);
        else if (driver.YearsLicensed >= 10) Apply("Licensed for 10 years or more", -0.08m);

        if (driver.Incidents > 0) Apply($"{driver.Incidents} incident(s) in the last 5 years", 0.18m * driver.Incidents);
        else Apply("Clean record", -0.10m);

        var vehicleAge = Math.Max(0, currentYear - vehicle.Year);
        if (vehicleAge <= 2) Apply("Vehicle 2 years old or newer", 0.12m);
        else if (vehicleAge >= 12) Apply("Vehicle 12 years old or older", -0.10m);

        if (vehicle.AnnualMiles > 15000) Apply("More than 15,000 miles a year", 0.10m);
        else if (vehicle.AnnualMiles < 7500) Apply("Fewer than 7,500 miles a year", -0.07m);

        annual = Math.Max(360m, Math.Round(annual, 2));
        var monthly = Math.Round(annual / 12m, 2);
        return new PriceResult(monthly, annual, lines);
    }
}

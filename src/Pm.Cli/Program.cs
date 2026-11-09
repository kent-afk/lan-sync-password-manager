using Pm.Cli.Commands;

try
{
    return await Cli.RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("ошибка: " + ex.Message);
    return 1;
}

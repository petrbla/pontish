using System;

internal static class Program
{
    private static void Main()
    {
        string pass = Environment.GetEnvironmentVariable("PONTISH_PASS") 
            ?? Environment.GetEnvironmentVariable("SSH_MOSTY_PASS");
        if (pass != null)
        {
            Console.WriteLine(pass);
        }
    }
}


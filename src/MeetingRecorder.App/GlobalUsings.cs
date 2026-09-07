// The WindowsDesktop SDK's implicit usings for a WPF project do not include
// System.IO (unlike the base Microsoft.NET.Sdk set), which is easy to trip over
// and produces a confusing "The name 'Path' does not exist" error. Declaring it
// once here keeps every file in this project consistent with the rest of the
// solution.
global using System.IO;

using System.Runtime.CompilerServices;

// The scanners, the name cleaner and the log file are internal on purpose: nothing outside
// Core calls them. The tests do, and this line came across from the WinUI project's own
// AssemblyInfo when Core was extracted.
[assembly: InternalsVisibleTo("Codec.Tests")]

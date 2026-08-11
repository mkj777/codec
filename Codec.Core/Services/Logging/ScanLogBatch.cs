using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Codec.Services.Logging
{
    public sealed class ScanLogBatch
    {
        private readonly string _gameName;
        private readonly string _source;
        private readonly List<string> _lines = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _flushed;

        public ScanLogBatch(string gameName, string source)
        {
            _gameName = string.IsNullOrWhiteSpace(gameName) ? "(unnamed)" : gameName;
            _source = string.IsNullOrWhiteSpace(source) ? "(unknown)" : source;
        }

        public bool IsFlushed => _flushed;

        public void Log(string line)
        {
            if (_flushed) return;
            _lines.Add(line);
        }

        public void Flush(string outcomeSymbol, string outcomeMessage)
        {
            if (_flushed) return;
            _flushed = true;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"┌─ [GAME] {_gameName}  source={_source}");
            foreach (var l in _lines)
            {
                sb.AppendLine($"│  {l}");
            }
            sb.Append($"└─ {outcomeSymbol} {outcomeMessage}  ({_sw.ElapsedMilliseconds}ms)");

            string text = sb.ToString();
            Debug.WriteLine(text);

            if (IsAddedOutcome(outcomeSymbol))
                ScanLogFile.WriteAdded(_source, text);
            else
                ScanLogFile.WriteRejected(_source, text);
        }

        private static bool IsAddedOutcome(string symbol) =>
            !string.IsNullOrEmpty(symbol) && symbol.StartsWith("✓");
    }
}

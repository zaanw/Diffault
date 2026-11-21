<?php
declare(strict_types=1);

$pattern = '~^(?:\[[^\]]+\]\s*)?(?:PHP\s+)?(Fatal error|Parse error|Warning|Notice|Deprecated):\s*(.+?)(?:\s+in\s+(.+?\.php)(?::|\s+on line\s+)(\d+))?$~i';
$diagnostics = [];
while (($line = fgets(STDIN)) !== false) {
    $original = rtrim($line, "\r\n");
    if (!preg_match($pattern, $original, $fields)) {
        if (isset($pending) && preg_match('~^\s*(?:Stack trace:|#\d+\s+|thrown in\s+)~', $original)) {
            $diagnostics[$pending]['original'] .= "\n" . $original;
        } else {
            unset($pending);
        }
        continue;
    }
    $severity = match (strtolower($fields[1])) {
        'fatal error', 'parse error' => 'error',
        'notice' => 'note',
        default => 'warning',
    };
    $message = $fields[2];
    $code = preg_match('/^Uncaught\s+([\w\\\\]+)(?::|\s)/', $message, $exception) ? $exception[1] : null;
    $lineNumber = isset($fields[4]) ? filter_var($fields[4], FILTER_VALIDATE_INT, [
        'options' => ['min_range' => 1, 'max_range' => 2147483647],
    ]) : false;
    $pending = count($diagnostics);
    $diagnostics[] = [
        'language' => 'php', 'severity' => $severity, 'file' => $fields[3] ?? null,
        'line' => $lineNumber === false ? null : $lineNumber, 'column' => null,
        'code' => $code, 'message' => $message, 'original' => $original, 'count' => 1,
    ];
}
echo json_encode($diagnostics, JSON_THROW_ON_ERROR | JSON_INVALID_UTF8_SUBSTITUTE), PHP_EOL;

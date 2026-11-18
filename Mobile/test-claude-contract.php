<?php
// Local CLI test for the optional Claude contract whitelist. Not deployed (not in any bundle list).
declare(strict_types=1);
if (PHP_SAPI !== 'cli') { http_response_code(404); exit; }
const PREFIX = "<?php exit; ?>\n";
require __DIR__ . '/notification-history.php';
$results = [];
function check(string $name, bool $ok): void { global $results; $results[] = ($ok ? 'PASS ' : 'FAIL ') . $name; }
function rejects(callable $fn): bool { try { $fn(); return false; } catch (InvalidArgumentException $e) { return true; } }
$now = time();
$iso = fn(int $offset) => gmdate('c', $now + $offset);
$details = ['syncedAt' => $iso(0),
    'pools' => [['label' => '5H', 'model' => null, 'used' => 16, 'resetsAt' => $iso(3600), 'allowance' => null],
                ['label' => '7D', 'model' => 'Fable', 'used' => 0, 'resetsAt' => $iso(86400), 'allowance' => 50]],
    'resets' => ['available' => 2, 'eligible' => true, 'syncedAt' => $iso(0), 'grants' => [
        ['kind' => 'fable', 'left' => 1, 'total' => 1, 'expiresAt' => $iso(5 * 86400), 'usableNow' => true],
        ['kind' => 'full', 'left' => 1, 'total' => 1, 'expiresAt' => null, 'usableNow' => true]]],
    'models' => ['count' => 13, 'primary' => ['Claude Fable 5.2', 'Claude Opus 5.5'], 'syncedAt' => $iso(0)],
    'extraUsage' => ['enabled' => false, 'used' => 0, 'limit' => null, 'currency' => 'USD', 'limitReached' => false]];
$clean = cleanClaudeDetails($details);
check('valid_details_stored', $clean['pools'][1]['model'] === 'Fable' && $clean['resets']['available'] === 2 &&
    $clean['resets']['grants'][1]['expiresAt'] === null && $clean['models']['primary'][0] === 'Claude Fable 5.2');
check('null_details_allowed_for_old_desktop', cleanClaudeDetails(null) === null);
check('unsafe_model_name_dropped', cleanClaudeDetails(['pools' => [['label' => '7D', 'model' => '<script>']]])['pools'][0]['model'] === null);
check('extra_keys_not_stored', !array_key_exists('label', cleanClaudeDetails(['resets' => ['grants' => [['kind' => 'full', 'label' => 'secret', 'id' => 'x']]]])['resets']['grants'][0]));
check('bad_pool_label_rejected', rejects(fn() => cleanClaudeDetails(['pools' => [['label' => '1M']]])));
check('bad_percent_rejected', rejects(fn() => cleanClaudeDetails(['pools' => [['label' => '5H', 'used' => 101]]])));
check('bad_grant_kind_rejected', rejects(fn() => cleanClaudeDetails(['resets' => ['grants' => [['kind' => 'gift']]]])));
check('bad_currency_rejected', rejects(fn() => cleanClaudeDetails(['extraUsage' => ['enabled' => true, 'currency' => 'usd$']])));
check('pools_capped_at_8', count(cleanClaudeDetails(['pools' => array_fill(0, 20, ['label' => '7D'])])['pools']) === 8);
$base = ['id' => str_repeat('a', 32), 'at' => $iso(0), 'remaining' => 0, 'creditCount' => 3, 'addedCount' => 1, 'plan' => 'Ultra'];
$claudeCredit = cleanNotifications([$base + ['provider' => 'claude', 'kind' => 'ResetCreditsAdded', 'window' => '', 'creditKind' => 'full', 'creditExpirations' => [$iso(100), $iso(100)]]], $now);
check('claude_credit_event_and_ultra_plan_accepted', $claudeCredit[0]['creditKind'] === 'full' && $claudeCredit[0]['plan'] === 'Ultra' && count($claudeCredit[0]['creditExpirations']) === 2);
$pool = cleanNotifications([['id' => str_repeat('b', 32), 'provider' => 'claude', 'kind' => 'LowRemaining', 'window' => '7D', 'pool' => 'Fable'] + $base], $now);
check('pool_event_accepted', $pool[0]['pool'] === 'Fable');
$old = cleanNotifications([['id' => str_repeat('c', 32), 'provider' => 'codex', 'kind' => 'LowRemaining', 'window' => '5H'] + array_diff_key($base, ['plan' => 1])], $now);
check('old_event_without_new_fields_accepted', $old[0]['pool'] === null && $old[0]['creditKind'] === null);
check('unknown_credit_kind_rejected', rejects(fn() => cleanNotifications([$base + ['provider' => 'claude', 'kind' => 'ResetCreditsAdded', 'window' => '', 'creditKind' => 'gift']], $now)));
check('unknown_plan_rejected', rejects(fn() => cleanNotifications([['plan' => 'Max 99x', 'provider' => 'claude', 'kind' => 'LowRemaining', 'window' => '7D'] + $base], $now)));
$failed = count(array_filter($results, fn($r) => strpos($r, 'FAIL') === 0));
echo implode("\n", $results), "\nSUMMARY passed=", $failed === 0 ? 'true' : 'false', " checks=", count($results), " failed=", $failed, "\n";
exit($failed === 0 ? 0 : 1);

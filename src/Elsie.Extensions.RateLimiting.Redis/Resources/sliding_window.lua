-- Sliding window rate limit via a sorted set of per-request timestamps.
-- KEYS[1] : full rate-limit key
-- ARGV[1] : permit limit
-- ARGV[2] : now (unix millis)
-- ARGV[3] : window (millis)
-- ARGV[4] : unique member token (used only on acquire)
-- ARGV[5] : '1' acquire, '0' peek
-- Returns : { limited(0|1), retry_after_millis, count }
local cutoff = tonumber(ARGV[2]) - tonumber(ARGV[3])
redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, cutoff)
local count = redis.call('ZCARD', KEYS[1])
local limited = 0
local retryAfterMillis = 0
if count < tonumber(ARGV[1]) then
    if ARGV[5] == '1' then
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[4])
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[3]) + 5000)
        count = count + 1
    end
else
    limited = 1
    local first = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
    local oldest = tonumber(first[2])
    if oldest ~= nil then
        retryAfterMillis = oldest + tonumber(ARGV[3]) - tonumber(ARGV[2])
        if retryAfterMillis < 0 then
            retryAfterMillis = 0
        end
    end
end
return { limited, retryAfterMillis, count }

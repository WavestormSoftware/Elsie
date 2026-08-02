-- Token bucket via hash { tokens, ts } with lazy refill.
-- KEYS[1] : full rate-limit key
-- ARGV[1] : capacity
-- ARGV[2] : tokens per second (fractional string)
-- ARGV[3] : now (unix seconds, fractional)
-- ARGV[4] : unused (reserved)
-- ARGV[5] : '1' acquire, '0' peek
-- Returns : { limited(0|1), floor(tokens), retry_after_millis }
local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens
local ts
if data[1] == false then
    tokens = tonumber(ARGV[1])
    ts = tonumber(ARGV[3])
else
    tokens = tonumber(data[1])
    ts = tonumber(data[2])
    local elapsed = tonumber(ARGV[3]) - ts
    if elapsed > 0 then
        tokens = math.min(tonumber(ARGV[1]), tokens + elapsed * tonumber(ARGV[2]))
        ts = tonumber(ARGV[3])
    end
end
local limited = 0
local retryAfterMillis = 0
if tokens < 1 then
    limited = 1
    retryAfterMillis = math.floor(((1 - tokens) / tonumber(ARGV[2])) * 1000) + 1
else
    if ARGV[5] == '1' then
        tokens = tokens - 1
    end
end
if ARGV[5] == '1' then
    redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts)
    redis.call('EXPIRE', KEYS[1], 3600)
end
return { limited, math.floor(tokens), retryAfterMillis }

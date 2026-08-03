-- Fixed-window rate limit: INCR + EXPIRE, window starts at first request.
-- KEYS[1] : full rate-limit key (prefix + partition)
-- ARGV[1] : permit limit
-- ARGV[2] : window seconds
-- ARGV[3] : now (unix seconds) — used only for peek reset calculation
-- ARGV[4] : '1' acquire (INCR), '0' peek (GET)
-- Returns : { count, ttl_seconds, limited(0|1) }
local current
if ARGV[4] == '1' then
    current = redis.call('INCR', KEYS[1])
    if current == 1 then
        redis.call('EXPIRE', KEYS[1], ARGV[2])
    end
else
    current = redis.call('GET', KEYS[1])
    if current == false then
        current = 0
    end
end
current = tonumber(current)
local ttl = redis.call('TTL', KEYS[1])
local limited = 0
if current > tonumber(ARGV[1]) then
    limited = 1
end
return { current, ttl, limited }

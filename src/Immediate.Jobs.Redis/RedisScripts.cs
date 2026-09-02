namespace Immediate.Jobs.Redis;

internal static class RedisScripts
{
	internal const string Enqueue =
		"""
		if redis.call('EXISTS', KEYS[1]) == 1 then
			return 0
		end
		redis.call('HSET', KEYS[1],
			'record', ARGV[1], 'state', ARGV[2], 'due', ARGV[3], 'dueScore', ARGV[16],
			'created', ARGV[17], 'dueMember', ARGV[18],
			'attempt', ARGV[4], 'worker', ARGV[5], 'lease', ARGV[6],
			'error', ARGV[7], 'completed', ARGV[8],
			'executionTraceId', ARGV[9], 'executionSpanId', ARGV[10],
			'executionStartedAt', ARGV[11], 'queue', ARGV[12], 'jobName', ARGV[13])
		redis.call('ZADD', KEYS[2], ARGV[14], ARGV[15])
		redis.call('SADD', KEYS[3], ARGV[15])
		if ARGV[2] == '2' or ARGV[2] == '3' then
			redis.call('ZADD', KEYS[4], ARGV[16], ARGV[18])
		end
		return 1
		""";

	internal const string Acquire =
		"""
		local function materialize(jobKey, indexKey, dataKey, attempt, state, completed)
			if attempt <= 0 or redis.call('HEXISTS', dataKey, attempt .. ':state') == 1 then return end
			local values = redis.call('HMGET', jobKey, 'worker', 'executionStartedAt',
				'executionTraceId', 'executionSpanId', 'error')
			redis.call('ZADD', indexKey, attempt, attempt)
			redis.call('HSET', dataKey,
				attempt .. ':state', state, attempt .. ':worker', values[1] or '',
				attempt .. ':acquired', '', attempt .. ':started', values[2] or '',
				attempt .. ':completed', completed or '', attempt .. ':trace', values[3] or '',
				attempt .. ':span', values[4] or '',
				attempt .. ':error', state == '2' and (values[5] or '') or '',
				attempt .. ':synthetic', '1')
		end
		local nowScore = tonumber(ARGV[1])
		local leaseScore = tonumber(ARGV[2])
		local leaseTicks = ARGV[3]
		local worker = ARGV[4]
		local batchSize = tonumber(ARGV[5])
		local queueCount = tonumber(ARGV[6])
		local root = ARGV[7]
		local queues = {}
		local nowTicks = ARGV[8]
		local position = 9
		for queueIndex = 1, queueCount do
			local queueName = ARGV[position]
			local capacity = tonumber(ARGV[position + 1])
			local jobCount = tonumber(ARGV[position + 2])
			position = position + 3
			local jobCapacities = {}
			for jobIndex = 1, jobCount do
				jobCapacities[ARGV[position]] = tonumber(ARGV[position + 1])
				position = position + 2
			end
			queues[queueIndex] = {
				name = queueName,
				capacity = capacity,
				jobCapacities = jobCapacities,
				dueKey = KEYS[4 + queueIndex]
			}
		end

		local dueByQueue = {}
		for queueIndex = 1, queueCount do
			dueByQueue[queues[queueIndex].name] = queues[queueIndex].dueKey
		end

		local expired = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', nowScore)
		for _, id in ipairs(expired) do
			local jobKey = root .. 'job:' .. id
			local state = redis.call('HGET', jobKey, 'state')
			local queueName = redis.call('HGET', jobKey, 'queue')
			local dueKey = dueByQueue[queueName]
			if state == '4' and dueKey then
				local attempt = tonumber(redis.call('HGET', jobKey, 'attempt') or '0')
				local lease = redis.call('HGET', jobKey, 'lease') or ''
				local indexKey = root .. 'executions:index:' .. id
				local dataKey = root .. 'executions:data:' .. id
				materialize(jobKey, indexKey, dataKey, attempt, '0', '')
				if attempt > 0 then
					redis.call('HSET', dataKey,
						attempt .. ':state', '4', attempt .. ':completed', lease, attempt .. ':error', '')
				end
				redis.call('HSET', jobKey, 'state', '3', 'worker', '', 'lease', '')
				redis.call('SREM', KEYS[2], id)
				redis.call('SADD', KEYS[3], id)
				local dueValues = redis.call('HMGET', jobKey, 'dueScore', 'dueMember')
				redis.call('ZADD', dueKey, dueValues[1] or nowScore, dueValues[2] or id)
				redis.call('ZREM', KEYS[1], id)
			elseif not state or state ~= '4' then
				redis.call('ZREM', KEYS[1], id)
			end
		end

		local acquired = {}
		for queueIndex = 1, queueCount do
			if #acquired >= batchSize then
				break
			end
			local queue = queues[queueIndex]
			local remaining = math.min(queue.capacity, batchSize - #acquired)
			if remaining > 0 then
				local selected = {}
				local stale = {}
				local offset = 0
				local chunkSize = math.min(256, math.max(64, remaining * 4))
				while remaining > 0 and #acquired + #selected < batchSize do
					local candidates = redis.call(
						'ZRANGEBYSCORE', queue.dueKey, '-inf', nowScore, 'LIMIT', offset, chunkSize)
					if #candidates == 0 then break end
					for _, member in ipairs(candidates) do
						if remaining <= 0 or #acquired + #selected >= batchSize then break end
						local id = string.sub(member, 41)
						local jobKey = root .. 'job:' .. id
						local values = redis.call('HMGET', jobKey, 'state', 'queue', 'jobName')
						local state = values[1]
						local jobCapacity = queue.jobCapacities[values[3]]
						if not state or values[2] ~= queue.name or (state ~= '2' and state ~= '3') then
							table.insert(stale, member)
						elseif jobCapacity and jobCapacity > 0 then
							table.insert(selected, { id = id, member = member, state = state, jobName = values[3] })
							queue.jobCapacities[values[3]] = jobCapacity - 1
							remaining = remaining - 1
						end
					end
					offset = offset + #candidates
					if #candidates < chunkSize then break end
				end
				for _, member in ipairs(stale) do redis.call('ZREM', queue.dueKey, member) end
				for _, candidate in ipairs(selected) do
					local jobKey = root .. 'job:' .. candidate.id
					local previousAttempt = tonumber(redis.call('HGET', jobKey, 'attempt') or '0')
					local indexKey = root .. 'executions:index:' .. candidate.id
					local dataKey = root .. 'executions:data:' .. candidate.id
					local previousError = redis.call('HGET', jobKey, 'error') or ''
					materialize(jobKey, indexKey, dataKey, previousAttempt,
						previousError ~= '' and '2' or '4', '')
					local attempt = previousAttempt + 1
					redis.call('HSET', jobKey,
						'state', '4', 'attempt', attempt, 'worker', worker, 'lease', leaseTicks,
						'executionTraceId', '', 'executionSpanId', '', 'executionStartedAt', '')
					redis.call('ZREM', queue.dueKey, candidate.member)
					redis.call('ZADD', KEYS[1], leaseScore, candidate.id)
					redis.call('SREM', candidate.state == '2' and KEYS[4] or KEYS[3], candidate.id)
					redis.call('SADD', KEYS[2], candidate.id)
					redis.call('ZADD', indexKey, attempt, attempt)
					redis.call('HSET', dataKey,
						attempt .. ':state', '0', attempt .. ':worker', worker,
						attempt .. ':acquired', nowTicks, attempt .. ':started', '',
						attempt .. ':completed', '', attempt .. ':trace', '', attempt .. ':span', '',
						attempt .. ':error', '', attempt .. ':synthetic', '0')
					table.insert(acquired, candidate.id)
				end
			end
		end
		return acquired
		""";

	internal const string SetTelemetry =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'attempt')
		if values[1] ~= '4' or values[2] ~= ARGV[1] or values[3] ~= ARGV[2] then return -1 end
		local field = ARGV[2] .. ':'
		if redis.call('HEXISTS', KEYS[3], field .. 'state') == 0 then
			redis.call('ZADD', KEYS[2], ARGV[2], ARGV[2])
			redis.call('HSET', KEYS[3], field .. 'state', '0', field .. 'worker', ARGV[1],
				field .. 'acquired', '', field .. 'completed', '', field .. 'error', '', field .. 'synthetic', '1')
		end
		redis.call('HSET', KEYS[1],
			'executionTraceId', ARGV[3], 'executionSpanId', ARGV[4], 'executionStartedAt', ARGV[5])
		redis.call('HSET', KEYS[3],
			field .. 'trace', ARGV[3], field .. 'span', ARGV[4], field .. 'started', ARGV[5])
		return 1
		""";

	internal const string RenewLease =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'attempt')
		if values[1] ~= '4' or values[2] ~= ARGV[1] or values[3] ~= ARGV[2] then return -1 end
		redis.call('HSET', KEYS[1], 'lease', ARGV[3])
		redis.call('ZADD', KEYS[2], ARGV[4], ARGV[5])
		return 1
		""";

	internal const string Complete =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'attempt',
			'executionStartedAt', 'executionTraceId', 'executionSpanId')
		if values[1] ~= '4' or values[2] ~= ARGV[1] or values[3] ~= ARGV[2] then return -1 end
		local field = ARGV[2] .. ':'
		if redis.call('HEXISTS', KEYS[7], field .. 'state') == 0 then
			redis.call('ZADD', KEYS[6], ARGV[2], ARGV[2])
			redis.call('HSET', KEYS[7], field .. 'worker', ARGV[1], field .. 'acquired', '',
				field .. 'started', values[4] or '', field .. 'trace', values[5] or '',
				field .. 'span', values[6] or '', field .. 'synthetic', '1')
		end
		redis.call('HSET', KEYS[7], field .. 'state', '1', field .. 'completed', ARGV[3], field .. 'error', '')
		redis.call('HSET', KEYS[1],
			'state', '5', 'worker', '', 'lease', '', 'error', '', 'completed', ARGV[3])
		redis.call('ZREM', KEYS[2], ARGV[4])
		redis.call('SREM', KEYS[3], ARGV[4])
		redis.call('SADD', KEYS[4], ARGV[4])
		redis.call('ZADD', KEYS[5], ARGV[5], ARGV[4])
		return 1
		""";

	internal const string Fail =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'queue', 'created', 'attempt',
			'executionStartedAt', 'executionTraceId', 'executionSpanId')
		if values[1] ~= '4' or values[2] ~= ARGV[1] or values[5] ~= ARGV[2] then return -1 end
		local field = ARGV[2] .. ':'
		if redis.call('HEXISTS', KEYS[9], field .. 'state') == 0 then
			redis.call('ZADD', KEYS[8], ARGV[2], ARGV[2])
			redis.call('HSET', KEYS[9], field .. 'worker', ARGV[1], field .. 'acquired', '',
				field .. 'started', values[6] or '', field .. 'trace', values[7] or '',
				field .. 'span', values[8] or '', field .. 'synthetic', '1')
		end
		redis.call('HSET', KEYS[9], field .. 'state', '2', field .. 'completed', ARGV[6], field .. 'error', ARGV[5])
		redis.call('ZREM', KEYS[2], ARGV[3])
		redis.call('SREM', KEYS[3], ARGV[3])
		if ARGV[4] == '' then
			redis.call('HSET', KEYS[1],
				'state', '6', 'worker', '', 'lease', '', 'error', ARGV[5], 'completed', ARGV[6])
			redis.call('SADD', KEYS[4], ARGV[3])
			redis.call('ZADD', KEYS[5], ARGV[7], ARGV[3])
		else
			local nextState = '2'
			if tonumber(ARGV[8]) <= tonumber(ARGV[9]) then nextState = '3' end
			redis.call('HSET', KEYS[1],
				'state', nextState, 'due', ARGV[4], 'dueScore', ARGV[8],
				'dueMember', ARGV[4] .. '|' .. values[4] .. '|' .. ARGV[3],
				'worker', '', 'lease', '', 'error', ARGV[5], 'completed', '')
			redis.call('SADD', nextState == '2' and KEYS[6] or KEYS[7], ARGV[3])
			redis.call('ZADD', ARGV[10] .. 'due:' .. values[3], ARGV[8],
				ARGV[4] .. '|' .. values[4] .. '|' .. ARGV[3])
		end
		return 1
		""";

	internal const string Cancel =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'queue', 'dueMember', 'attempt',
			'worker', 'executionStartedAt', 'executionTraceId', 'executionSpanId')
		local state = values[1]
		if state == '5' or state == '6' or state == '7' or state == '8' then return -1 end
		local attempt = tonumber(values[4] or '0')
		if state == '4' and attempt > 0 then
			local field = tostring(attempt) .. ':'
			if redis.call('HEXISTS', KEYS[4], field .. 'state') == 0 then
				redis.call('ZADD', KEYS[3], attempt, attempt)
				redis.call('HSET', KEYS[4],
					field .. 'worker', values[5] or '', field .. 'acquired', '',
					field .. 'started', values[6] or '', field .. 'trace', values[7] or '',
					field .. 'span', values[8] or '', field .. 'synthetic', '1')
			end
			redis.call('HSET', KEYS[4],
				field .. 'state', '3', field .. 'completed', ARGV[2], field .. 'error', '')
		end
		redis.call('HSET', KEYS[1],
			'state', '7', 'worker', '', 'lease', '', 'error', '', 'completed', ARGV[2])
		redis.call('SREM', ARGV[4] .. 'state:' .. state, ARGV[1])
		redis.call('SADD', ARGV[4] .. 'state:7', ARGV[1])
		redis.call('ZREM', KEYS[2], ARGV[1])
		if values[3] then redis.call('ZREM', ARGV[4] .. 'due:' .. values[2], values[3]) end
		redis.call('ZADD', ARGV[4] .. 'completed:7', ARGV[3], ARGV[1])
		return 1
		""";

	internal const string Retry =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'queue', 'created', 'dueMember',
			'attempt', 'worker', 'executionStartedAt', 'executionTraceId', 'executionSpanId', 'error', 'completed')
		local failed = values[1] == '6'
		local scheduled = values[1] == '2'
		if not failed and not scheduled then return -1 end
		local attempt = tonumber(values[5] or '0')
		local field = tostring(attempt) .. ':'
		if attempt > 0 and redis.call('HEXISTS', KEYS[7], field .. 'state') == 0 then
			local executionState = (failed or (values[10] and values[10] ~= '')) and '2' or '4'
			redis.call('ZADD', KEYS[6], attempt, attempt)
			redis.call('HSET', KEYS[7], field .. 'state', executionState,
				field .. 'worker', values[6] or '', field .. 'acquired', '',
				field .. 'started', values[7] or '', field .. 'completed', values[11] or '',
				field .. 'trace', values[8] or '', field .. 'span', values[9] or '',
				field .. 'error', executionState == '2' and (values[10] or '') or '',
				field .. 'synthetic', '1')
		end
		redis.call('HSET', KEYS[1],
			'state', '3', 'due', ARGV[1], 'dueScore', ARGV[2],
			'dueMember', ARGV[1] .. '|' .. values[3] .. '|' .. ARGV[3],
			'worker', '', 'lease', '')
		if failed then
			redis.call('HSET', KEYS[1], 'error', '', 'completed', '')
			redis.call('SREM', KEYS[2], ARGV[3])
			redis.call('ZREM', KEYS[5], ARGV[3])
		else
			redis.call('SREM', KEYS[3], ARGV[3])
			if values[4] then redis.call('ZREM', ARGV[4] .. 'due:' .. values[2], values[4]) end
		end
		redis.call('SADD', KEYS[4], ARGV[3])
		redis.call('ZADD', ARGV[4] .. 'due:' .. values[2], ARGV[2],
			ARGV[1] .. '|' .. values[3] .. '|' .. ARGV[3])
		return 1
		""";

	internal const string Delete =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'recurringKey')
		local state = values[1]
		if state ~= '5' and state ~= '6' and state ~= '7' and state ~= '8' then return -1 end
		redis.call('DEL', KEYS[1])
		redis.call('DEL', KEYS[4], KEYS[5])
		redis.call('ZREM', KEYS[2], ARGV[1])
		redis.call('SREM', ARGV[2] .. 'state:' .. state, ARGV[1])
		redis.call('ZREM', ARGV[2] .. 'completed:' .. state, ARGV[1])
		if values[2] and values[2] ~= '' then redis.call('HDEL', KEYS[3], values[2]) end
		return 1
		""";

	internal const string Purge =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then
			redis.call('DEL', KEYS[6], KEYS[7])
			redis.call('ZREM', KEYS[2], ARGV[1])
			return 0
		end
		local values = redis.call('HMGET', KEYS[1], 'state', 'completed', 'recurringKey')
		if values[1] ~= ARGV[2] then
			redis.call('ZREM', KEYS[2], ARGV[1])
			return 0
		end
		redis.call('DEL', KEYS[1])
		redis.call('DEL', KEYS[6], KEYS[7])
		redis.call('ZREM', KEYS[2], ARGV[1])
		redis.call('ZREM', KEYS[3], ARGV[1])
		redis.call('SREM', KEYS[4], ARGV[1])
		if values[3] and values[3] ~= '' then redis.call('HDEL', KEYS[5], values[3]) end
		return 1
		""";

	internal const string UpsertRecurring =
		"""
		local exists = redis.call('EXISTS', KEYS[1]) == 1
		local paused = ARGV[3]
		local last = ARGV[5]
		local next = ARGV[4]
		local dueScore = ARGV[7]
		local dueMember = ARGV[8]
		if exists then
			local current = redis.call('HMGET', KEYS[1], 'code', 'paused', 'last', 'dueMember', 'cron', 'timeZone', 'record', 'next', 'dueScore')
			if current[1] == '1' and ARGV[2] == '0' then return -1 end
			paused = current[2] or paused
			last = current[3] or last
			if current[4] then redis.call('ZREM', KEYS[3], current[4]) end
			if ARGV[11] == '1' then
				local currentCron = current[5]
				local currentTimeZone = current[6]
				if (not currentCron or not currentTimeZone) and current[7] then
					local record = cjson.decode(current[7])
					currentCron = record.Cron
					currentTimeZone = record.TimeZone
				end
				if currentCron == ARGV[9] and currentTimeZone == ARGV[10] then
					next = current[8] or next
					dueScore = current[9] or dueScore
					dueMember = current[4] or dueMember
				end
			end
		end
		redis.call('HSET', KEYS[1],
			'record', ARGV[1], 'code', ARGV[2], 'paused', paused,
			'next', next, 'last', last, 'dueScore', dueScore, 'dueMember', dueMember,
			'cron', ARGV[9], 'timeZone', ARGV[10])
		redis.call('SADD', KEYS[2], ARGV[6])
		if paused == '1' then
			redis.call('ZREM', KEYS[3], dueMember)
		else
			redis.call('ZADD', KEYS[3], dueScore, dueMember)
		end
		return 1
		""";

	internal const string RemoveObsoleteRecurring =
		"""
		local active = {}
		for index = 2, #ARGV do active[ARGV[index]] = true end
		local names = redis.call('SMEMBERS', KEYS[1])
		local removed = 0
		for _, name in ipairs(names) do
			local key = ARGV[1] .. 'recurring:' .. name
			if redis.call('HGET', key, 'code') == '1' and not active[name] then
				local dueMember = redis.call('HGET', key, 'dueMember')
				redis.call('DEL', key)
				redis.call('SREM', KEYS[1], name)
				if dueMember then redis.call('ZREM', KEYS[2], dueMember) end
				removed = removed + 1
			end
		end
		return removed
		""";

	internal const string RemoveRecurring =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		if redis.call('HGET', KEYS[1], 'code') == '1' then return -1 end
		local dueMember = redis.call('HGET', KEYS[1], 'dueMember')
		redis.call('DEL', KEYS[1])
		redis.call('SREM', KEYS[2], ARGV[1])
		if dueMember then redis.call('ZREM', KEYS[3], dueMember) end
		return 1
		""";

	internal const string SetRecurringPaused =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		redis.call('HSET', KEYS[1], 'paused', ARGV[1])
		local due = redis.call('HMGET', KEYS[1], 'dueScore', 'dueMember')
		if ARGV[1] == '1' then
			if due[2] then redis.call('ZREM', KEYS[2], due[2]) end
		elseif due[1] and due[2] then
			redis.call('ZADD', KEYS[2], due[1], due[2])
		end
		return 1
		""";

	internal const string MaterializeRecurring =
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local schedule = redis.call('HMGET', KEYS[1], 'next', 'paused', 'dueMember')
		if schedule[1] ~= ARGV[1] or schedule[2] == '1' then return 0 end
		local previousDueMember = schedule[3]
		if not previousDueMember or previousDueMember ~= ARGV[1] .. '|' .. ARGV[21] then return 0 end
		local previousDueScore = redis.call('ZSCORE', KEYS[7], previousDueMember)
		if not previousDueScore or tonumber(previousDueScore) > tonumber(ARGV[24]) then return 0 end
		local inserted = 1
		if ARGV[2] ~= '' then
			inserted = redis.call('HSETNX', KEYS[2], ARGV[2], ARGV[3])
		end
		if inserted == 1 then
			if redis.call('EXISTS', KEYS[3]) == 1 then
				if ARGV[2] ~= '' then redis.call('HDEL', KEYS[2], ARGV[2]) end
				return -1
			end
			redis.call('HSET', KEYS[3],
				'record', ARGV[4], 'state', ARGV[5], 'due', ARGV[6], 'dueScore', ARGV[7],
				'created', ARGV[22], 'dueMember', ARGV[6] .. '|' .. ARGV[22] .. '|' .. ARGV[3],
				'recurringKey', ARGV[2],
				'attempt', ARGV[8], 'worker', ARGV[9], 'lease', ARGV[10],
				'error', ARGV[11], 'completed', ARGV[12],
				'executionTraceId', ARGV[13], 'executionSpanId', ARGV[14],
				'executionStartedAt', ARGV[15], 'queue', ARGV[16], 'jobName', ARGV[17])
			redis.call('ZADD', KEYS[4], ARGV[18], ARGV[3])
			redis.call('SADD', KEYS[5], ARGV[3])
			if ARGV[5] == '2' or ARGV[5] == '3' then
				redis.call('ZADD', KEYS[6], ARGV[7], ARGV[6] .. '|' .. ARGV[22] .. '|' .. ARGV[3])
			elseif ARGV[5] == '7' or ARGV[5] == '8' then
				redis.call('ZADD', KEYS[8], ARGV[23], ARGV[3])
			end
		end
		local nextDueMember = ARGV[19] .. '|' .. ARGV[21]
		redis.call('HSET', KEYS[1],
			'last', ARGV[1], 'next', ARGV[19], 'dueScore', ARGV[20], 'dueMember', nextDueMember)
		if previousDueMember then redis.call('ZREM', KEYS[7], previousDueMember) end
		redis.call('ZADD', KEYS[7], ARGV[20], nextDueMember)
		return inserted
		""";

	internal const string Heartbeat =
		"""
		redis.call('HSET', KEYS[1],
			'last', ARGV[1], 'active', ARGV[2], 'max', ARGV[3])
		redis.call('PEXPIRE', KEYS[1], ARGV[6])
		redis.call('ZADD', KEYS[2], ARGV[4], ARGV[5])
		return 1
		""";
}

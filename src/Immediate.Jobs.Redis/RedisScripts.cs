namespace Immediate.Jobs.Redis;

internal static class RedisScripts
{
	private const string FairQueueFunctions =
		"""
		local function fairDueKey(root, queue, group)
			if group == '' then return root .. 'fair:ungrouped:' .. queue end
			return root .. 'fair:due:' .. string.len(queue) .. ':' .. queue .. ':' .. group
		end
		local function indexDue(root, queue, group, score, member)
			redis.call('ZADD', fairDueKey(root, queue, group), score, member)
			if group ~= '' then redis.call('SADD', root .. 'fair:groups:' .. queue, group) end
		end
		local function unindexDue(root, queue, group, member)
			local key = fairDueKey(root, queue, group)
			redis.call('ZREM', key, member)
			if group ~= '' and redis.call('ZCARD', key) == 0 then
				redis.call('SREM', root .. 'fair:groups:' .. queue, group)
			end
		end
		local function changeActive(root, queue, group, delta)
			local key = root .. 'fair:active:' .. queue
			if redis.call('HINCRBY', key, group, delta) <= 0 then redis.call('HDEL', key, group) end
		end
		local function removeClearedCursor(root, queue, group)
			if group == '' then return end
			if redis.call('HEXISTS', root .. 'fair:active:' .. queue, group) == 1 then return end
			if redis.call('ZCARD', fairDueKey(root, queue, group)) > 0 then return end
			redis.call('HDEL', root .. 'fair:cursor:' .. queue, group)
		end

		""";

	internal const string Enqueue =
		FairQueueFunctions +
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
			'executionStartedAt', ARGV[11], 'queue', ARGV[12], 'jobName', ARGV[13], 'group', ARGV[19])
		redis.call('ZADD', KEYS[2], ARGV[14], ARGV[15])
		redis.call('SADD', KEYS[3], ARGV[15])
		if ARGV[2] == '2' or ARGV[2] == '3' then
			redis.call('ZADD', KEYS[4], ARGV[16], ARGV[18])
			indexDue(ARGV[20], ARGV[12], ARGV[19], ARGV[16], ARGV[18])
		end
		return 1
		""";

	private const string DefinitionFunctions =
		"""
		local function addTicks(left, right)
			local carry = 0
			local result = ''
			for index = 19, 1, -1 do
				local value = tonumber(string.sub(left, index, index)) + tonumber(string.sub(right, index, index)) + carry
				result = tostring(value % 10) .. result
				carry = math.floor(value / 10)
			end
			return result
		end
		local function readLimits(args, position)
			return {
				slidingMax = tonumber(args[position]), slidingPeriod = args[position + 1], slidingCutoff = args[position + 2],
				fixedMax = tonumber(args[position + 3]), fixedStart = args[position + 4], fixedNext = args[position + 5],
				concurrency = tonumber(args[position + 6])
			}
		end
		local function definitionState(root, name, limits, nowScore, nowTicks, includeActiveCount)
			local key = root .. 'definitions:state:' .. name
			local paused = redis.call('HGET', key, 'paused') == '1'
			local next = ''
			if limits.slidingMax > 0 then
				local logKey = root .. 'definitions:acquisitions:' .. name
				redis.call('ZREMRANGEBYLEX', logKey, '-', '[' .. limits.slidingCutoff .. '~')
				local recent = redis.call('ZRANGE', logKey, 0, -1)
				if #recent >= limits.slidingMax then
					next = addTicks(string.sub(recent[#recent - limits.slidingMax + 1], 1, 19), limits.slidingPeriod)
				end
			end
			if limits.fixedMax > 0 then
				local values = redis.call('HMGET', key, 'fixedStart', 'fixedCount')
				if values[1] == limits.fixedStart and tonumber(values[2] or '0') >= limits.fixedMax then
					if next == '' or limits.fixedNext > next then next = limits.fixedNext end
				end
			end
			local activeCount = 0
			local concurrencyLimited = false
			if includeActiveCount or limits.concurrency > 0 then
				local leasesKey = root .. 'definitions:leases:' .. name
				redis.call('ZREMRANGEBYSCORE', leasesKey, '-inf', '(' .. nowScore)
				local ids = redis.call('ZRANGEBYSCORE', leasesKey, nowScore, '+inf')
				local count = 0
				for _, id in ipairs(ids) do
					local values = redis.call('HMGET', root .. 'job:' .. id, 'state', 'jobName', 'lease')
					if values[1] == '4' and values[2] == name and values[3] > nowTicks then
						count = count + 1
					else
						redis.call('ZREM', leasesKey, id)
					end
				end
				activeCount = count
				concurrencyLimited = limits.concurrency > 0 and count >= limits.concurrency
			end
			local status = paused and 1 or next ~= '' and 2 or concurrencyLimited and 3 or 0
			if paused or concurrencyLimited then next = '' end
			redis.call('HSET', key, 'status', status, 'next', next)
			return { status, next, paused and 1 or 0, activeCount }
		end
		local function recordDefinitionAcquisition(root, name, limits, nowTicks, id, attempt)
			local key = root .. 'definitions:state:' .. name
			if limits.slidingMax > 0 then
				redis.call('ZADD', root .. 'definitions:acquisitions:' .. name, 0, nowTicks .. '|' .. id .. '|' .. attempt)
			end
			if limits.fixedMax > 0 then
				if redis.call('HGET', key, 'fixedStart') ~= limits.fixedStart then
					redis.call('HSET', key, 'fixedStart', limits.fixedStart, 'fixedCount', '0')
				end
				redis.call('HINCRBY', key, 'fixedCount', 1)
			end
		end

		""";

	internal const string DefinitionStates = DefinitionFunctions +
		"""
		local results = {}
		for position = 4, #ARGV, 8 do
			results[#results + 1] = definitionState(ARGV[1], ARGV[position], readLimits(ARGV, position + 1), tonumber(ARGV[2]), ARGV[3], true)
		end
		return results
		""";

	internal const string DefinitionState = DefinitionFunctions +
		"""
		local root = ARGV[1]
		local name = ARGV[2]
		if ARGV[5] == '1' then redis.call('HSET', root .. 'definitions:state:' .. name, 'paused', '0') end
		return definitionState(root, name, readLimits(ARGV, 6), tonumber(ARGV[3]), ARGV[4], true)
		""";

	internal const string Acquire =
		DefinitionFunctions + FairQueueFunctions +
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
		local fair = ARGV[9] == '1'
		local concurrencyShareThreshold = tonumber(ARGV[10])
		local minInflightForNoisy = tonumber(ARGV[11])
		local groupRoundRobin = ARGV[12] == '1'
		local position = 13
		for queueIndex = 1, queueCount do
			local queueName = ARGV[position]
			local capacity = tonumber(ARGV[position + 1])
			local jobCount = tonumber(ARGV[position + 2])
			position = position + 3
			local jobCapacities = {}
			local jobLimits = {}
			for jobIndex = 1, jobCount do
				jobCapacities[ARGV[position]] = tonumber(ARGV[position + 1])
				jobLimits[ARGV[position]] = readLimits(ARGV, position + 2)
				position = position + 9
			end
			queues[queueIndex] = {
				name = queueName,
				capacity = capacity,
				jobCapacities = jobCapacities,
				jobLimits = jobLimits,
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
				local dueValues = redis.call('HMGET', jobKey, 'dueScore', 'dueMember', 'group')
				redis.call('ZADD', dueKey, dueValues[1] or nowScore, dueValues[2] or id)
				indexDue(root, queueName, dueValues[3] or '', dueValues[1] or nowScore, dueValues[2] or id)
				changeActive(root, queueName, dueValues[3] or '', -1)
				redis.call('ZREM', KEYS[1], id)
			elseif not state or state ~= '4' then
				redis.call('ZREM', KEYS[1], id)
			end
		end

		local acquired = {}
		local definitionStatuses = {}
		local function cachedDefinitionStatus(queue, name)
			local status = definitionStatuses[name]
			if status == nil then
				status = definitionState(root, name, queue.jobLimits[name], nowScore, nowTicks)[1]
				definitionStatuses[name] = status
			end
			return status
		end

		local function claim(queue, candidate)
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
			unindexDue(root, queue.name, candidate.group, candidate.member)
			changeActive(root, queue.name, candidate.group, 1)
			redis.call('ZADD', KEYS[1], leaseScore, candidate.id)
			redis.call('SREM', candidate.state == '2' and KEYS[4] or KEYS[3], candidate.id)
			redis.call('SADD', KEYS[2], candidate.id)
			redis.call('ZADD', indexKey, attempt, attempt)
			redis.call('HSET', dataKey,
				attempt .. ':state', '0', attempt .. ':worker', worker,
				attempt .. ':acquired', nowTicks, attempt .. ':started', '',
				attempt .. ':completed', '', attempt .. ':trace', '', attempt .. ':span', '',
				attempt .. ':error', '', attempt .. ':synthetic', '0')
			redis.call('ZADD', root .. 'definitions:leases:' .. candidate.jobName, leaseScore, candidate.id)
			recordDefinitionAcquisition(root, candidate.jobName, queue.jobLimits[candidate.jobName], nowTicks, candidate.id, attempt)
			definitionStatuses[candidate.jobName] = nil
			cachedDefinitionStatus(queue, candidate.jobName)
			table.insert(acquired, candidate.id)
		end

		local function fairHead(queue, group)
			local key = fairDueKey(root, queue.name, group)
			local offset = 0
			while true do
				local members = redis.call('ZRANGEBYSCORE', key, '-inf', nowScore, 'LIMIT', offset, 64)
				local stale = {}
				local head = nil
				for _, member in ipairs(members) do
					local id = string.sub(member, 41)
					local name = redis.call('HGET', root .. 'job:' .. id, 'jobName')
					if not name or definitionStatuses[name] == nil or definitionStatuses[name] == 0 then
						local values = redis.call('HMGET', root .. 'job:' .. id, 'state', 'queue', 'jobName', 'dueMember')
						local state = values[1]
						local jobCapacity = queue.jobCapacities[values[3]]
						if not state or values[2] ~= queue.name or (state ~= '2' and state ~= '3') or values[4] ~= member then
							table.insert(stale, member)
						elseif jobCapacity and jobCapacity > 0 and cachedDefinitionStatus(queue, values[3]) == 0 then
							head = { id = id, member = member, state = state, jobName = values[3], group = group }
							break
						end
					end
				end
				for _, member in ipairs(stale) do redis.call('ZREM', key, member) end
				if head or #members < 64 then
					if not head and group ~= '' and redis.call('ZCARD', key) == 0 then
						redis.call('SREM', root .. 'fair:groups:' .. queue.name, group)
					end
					return head
				end
				offset = offset + #members - #stale
			end
		end

		local function acquireFairly(queue, remaining)
			local heads = {}
			local hasGroupedHead = false
			for _, group in ipairs(redis.call('SMEMBERS', root .. 'fair:groups:' .. queue.name)) do
				heads[group] = fairHead(queue, group)
				if heads[group] then hasGroupedHead = true end
			end
			if not hasGroupedHead then return false end
			heads[''] = fairHead(queue, '')

			local active = {}
			local totalActive = 0
			local activeValues = redis.call('HGETALL', root .. 'fair:active:' .. queue.name)
			for index = 1, #activeValues, 2 do
				local count = tonumber(activeValues[index + 1])
				active[activeValues[index]] = count
				totalActive = totalActive + count
			end

			local cursorKey = root .. 'fair:cursor:' .. queue.name
			local lastServed = {}
			local sequence = 0
			local cursorValues = redis.call('HGETALL', cursorKey)
			for index = 1, #cursorValues, 2 do
				local value = tonumber(cursorValues[index + 1])
				lastServed[cursorValues[index]] = value
				if value > sequence then sequence = value end
			end

			local function isNoisy(group)
				local count = active[group]
				return group ~= '' and count ~= nil and totalActive > 0 and count >= minInflightForNoisy
					and count / totalActive > concurrencyShareThreshold
			end

			local function before(left, right)
				local leftNoisy = isNoisy(left.group)
				local rightNoisy = isNoisy(right.group)
				if leftNoisy ~= rightNoisy then return not leftNoisy end
				local leftInflight = leftNoisy and active[left.group] or 0
				local rightInflight = rightNoisy and active[right.group] or 0
				if leftInflight ~= rightInflight then return leftInflight < rightInflight end
				if groupRoundRobin then
					local leftServed = lastServed[left.group] or 0
					local rightServed = lastServed[right.group] or 0
					if leftServed ~= rightServed then return leftServed < rightServed end
				end
				return left.member < right.member
			end

			while remaining > 0 and #acquired < batchSize do
				local candidate = nil
				for _, head in pairs(heads) do
					if not candidate or before(head, candidate) then candidate = head end
				end
				if not candidate then break end

				claim(queue, candidate)
				queue.jobCapacities[candidate.jobName] = queue.jobCapacities[candidate.jobName] - 1
				remaining = remaining - 1
				active[candidate.group] = (active[candidate.group] or 0) + 1
				totalActive = totalActive + 1
				if groupRoundRobin and candidate.group ~= '' then
					sequence = sequence + 1
					lastServed[candidate.group] = sequence
					redis.call('HSET', cursorKey, candidate.group, sequence)
				end

				for group, head in pairs(heads) do
					if group == candidate.group or queue.jobCapacities[head.jobName] <= 0 or
						cachedDefinitionStatus(queue, head.jobName) ~= 0 then
						heads[group] = fairHead(queue, group)
					end
				end
			end
			return true
		end

		for queueIndex = 1, queueCount do
			if #acquired >= batchSize then
				break
			end
			local queue = queues[queueIndex]
			local remaining = math.min(queue.capacity, batchSize - #acquired)
			if remaining > 0 and not (fair and acquireFairly(queue, remaining)) then
				local offset = 0
				local chunkSize = math.min(256, math.max(64, remaining * 4))
				while remaining > 0 and #acquired < batchSize do
					local candidates = redis.call('ZRANGEBYSCORE', queue.dueKey, '-inf', nowScore, 'LIMIT', offset, chunkSize)
					if #candidates == 0 then break end
					local removed = 0
					for _, member in ipairs(candidates) do
						if remaining <= 0 or #acquired >= batchSize then break end
						local id = string.sub(member, 41)
						local name = redis.call('HGET', root .. 'job:' .. id, 'jobName')
						if not name or definitionStatuses[name] == nil or definitionStatuses[name] == 0 then
							local values = redis.call('HMGET', root .. 'job:' .. id, 'state', 'queue', 'jobName', 'group')
							local state = values[1]
							local jobCapacity = queue.jobCapacities[values[3]]
							if not state or values[2] ~= queue.name or (state ~= '2' and state ~= '3') then
								redis.call('ZREM', queue.dueKey, member)
								removed = removed + 1
							elseif jobCapacity and jobCapacity > 0 and cachedDefinitionStatus(queue, values[3]) == 0 then
								claim(queue, { id = id, member = member, state = state, jobName = values[3], group = values[4] or '' })
								queue.jobCapacities[values[3]] = jobCapacity - 1
								remaining = remaining - 1
								removed = removed + 1
							end
						end
					end
					offset = offset + #candidates - removed
					if #candidates < chunkSize then break end
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
		redis.call('ZADD', ARGV[6] .. 'definitions:leases:' .. redis.call('HGET', KEYS[1], 'jobName'), ARGV[4], ARGV[5])
		return 1
		""";

	internal const string Complete =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'attempt',
			'executionStartedAt', 'executionTraceId', 'executionSpanId', 'queue', 'group')
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
		changeActive(ARGV[6], values[7], values[8] or '', -1)
		removeClearedCursor(ARGV[6], values[7], values[8] or '')
		return 1
		""";

	internal const string Fail =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'queue', 'created', 'attempt',
			'executionStartedAt', 'executionTraceId', 'executionSpanId', 'group')
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
		local group = values[9] or ''
		changeActive(ARGV[10], values[3], group, -1)
		if ARGV[4] == '' then
			redis.call('HSET', KEYS[1],
				'state', '6', 'worker', '', 'lease', '', 'error', ARGV[5], 'completed', ARGV[6])
			redis.call('SADD', KEYS[4], ARGV[3])
			redis.call('ZADD', KEYS[5], ARGV[7], ARGV[3])
			removeClearedCursor(ARGV[10], values[3], group)
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
			indexDue(ARGV[10], values[3], group, ARGV[8], ARGV[4] .. '|' .. values[4] .. '|' .. ARGV[3])
		end
		return 1
		""";

	internal const string UpdatePayload =
		"""
		local values = redis.call('HMGET', KEYS[1], 'record', 'state')
		if values[1] ~= ARGV[1] or values[2] ~= '1' then return 0 end
		redis.call('HSET', KEYS[1], 'record', ARGV[2])
		return 1
		""";

	internal const string Trigger =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'jobName', 'queue', 'created', 'group', 'record')
		if values[2] ~= ARGV[1] then return -1 end
		if values[1] ~= '1' then return -2 end
		if cjson.decode(values[6]).Payload == '' then return -3 end
		local state = tonumber(ARGV[3]) <= tonumber(ARGV[4]) and '3' or '2'
		local member = ARGV[2] .. '|' .. values[4] .. '|' .. ARGV[6]
		redis.call('HSET', KEYS[1], 'state', state, 'due', ARGV[2], 'dueScore', ARGV[3], 'dueMember', member)
		redis.call('SREM', ARGV[5] .. 'state:1', ARGV[6])
		redis.call('SADD', ARGV[5] .. 'state:' .. state, ARGV[6])
		redis.call('ZADD', ARGV[5] .. 'due:' .. values[3], ARGV[3], member)
		indexDue(ARGV[5], values[3], values[5] or '', ARGV[3], member)
		return 1
		""";

	internal const string Cancel =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'queue', 'dueMember', 'attempt',
			'worker', 'executionStartedAt', 'executionTraceId', 'executionSpanId', 'group')
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
		local group = values[9] or ''
		if state == '4' then changeActive(ARGV[4], values[2], group, -1) end
		if values[3] then unindexDue(ARGV[4], values[2], group, values[3]) end
		removeClearedCursor(ARGV[4], values[2], group)
		return 1
		""";

	internal const string Retry =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'queue', 'created', 'dueMember',
			'attempt', 'worker', 'executionStartedAt', 'executionTraceId', 'executionSpanId', 'error', 'completed',
			'group')
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
			if values[4] then
				redis.call('ZREM', ARGV[4] .. 'due:' .. values[2], values[4])
				unindexDue(ARGV[4], values[2], values[12] or '', values[4])
			end
		end
		redis.call('SADD', KEYS[4], ARGV[3])
		redis.call('ZADD', ARGV[4] .. 'due:' .. values[2], ARGV[2],
			ARGV[1] .. '|' .. values[3] .. '|' .. ARGV[3])
		indexDue(ARGV[4], values[2], values[12] or '', ARGV[2], ARGV[1] .. '|' .. values[3] .. '|' .. ARGV[3])
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

	internal const string MergeDefinitionCatalog =
		"""
		if (redis.call('GET', KEYS[2]) or '0') ~= ARGV[1] then return 0 end
		local changes = cjson.decode(ARGV[3])
		for _, name in ipairs(changes.RemovedDefinitions) do redis.call('HDEL', KEYS[1], name) end
		for _, definition in ipairs(changes.Definitions) do
			redis.call('HSET', KEYS[1], definition.Name, definition.Metadata)
		end
		for _, name in ipairs(changes.RemovedSchedules) do
			local key = ARGV[2] .. 'recurring:' .. name
			if redis.call('HGET', key, 'code') == '1' then
				local dueMember = redis.call('HGET', key, 'dueMember')
				if dueMember then redis.call('ZREM', KEYS[4], dueMember) end
				redis.call('DEL', key)
				redis.call('SREM', KEYS[3], name)
			end
		end
		for _, schedule in ipairs(changes.Schedules) do
			local key = ARGV[2] .. 'recurring:' .. schedule.Name
			local paused, last, next = schedule.Paused, schedule.Last, schedule.Next
			local dueScore, dueMember = schedule.DueScore, schedule.DueMember
			local current = redis.call('HMGET', key, 'paused', 'last', 'next', 'dueScore', 'dueMember', 'cron', 'timeZone')
			paused = current[1] or paused
			last = current[2] or last
			if current[5] then redis.call('ZREM', KEYS[4], current[5]) end
			if current[6] == schedule.Cron and current[7] == schedule.TimeZone then
				next = current[3] or next
				dueScore = current[4] or dueScore
				dueMember = current[5] or dueMember
			end
			redis.call('HSET', key, 'record', schedule.Record, 'code', '1', 'paused', paused,
				'next', next, 'last', last, 'dueScore', dueScore, 'dueMember', dueMember,
				'cron', schedule.Cron, 'timeZone', schedule.TimeZone, 'jobName', schedule.JobName)
			redis.call('SADD', KEYS[3], schedule.Name)
			if paused ~= '1' then redis.call('ZADD', KEYS[4], dueScore, dueMember) end
		end
		redis.call('INCR', KEYS[2])
		return 1
		""";

	internal const string GetDueRecurringForJobs =
		"""
		local eligible = {}
		for index = 4, #ARGV do eligible[ARGV[index]] = true end
		local result = {}
		if tonumber(ARGV[3]) <= 0 then return result end
		local offset = 0
		while true do
			local members = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[2], 'LIMIT', offset, 256)
			if #members == 0 then break end
			for _, member in ipairs(members) do
				local name = string.sub(member, 21)
				local key = ARGV[1] .. 'recurring:' .. name
				local current = redis.call('HMGET', key, 'record', 'paused', 'dueMember', 'jobName')
				if current[1] and current[2] ~= '1' and current[3] == member then
					local record = cjson.decode(current[1])
					if eligible[current[4] or string.upper(record.JobName)] then
						table.insert(result, member)
						if #result >= tonumber(ARGV[3]) then return result end
					end
				end
			end
			offset = offset + #members
		end
		return result
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
			'cron', ARGV[9], 'timeZone', ARGV[10], 'jobName', ARGV[12])
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
		FairQueueFunctions +
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
				'executionStartedAt', ARGV[15], 'queue', ARGV[16], 'jobName', ARGV[17], 'group', '')
			redis.call('ZADD', KEYS[4], ARGV[18], ARGV[3])
			redis.call('SADD', KEYS[5], ARGV[3])
			if ARGV[5] == '2' or ARGV[5] == '3' then
				redis.call('ZADD', KEYS[6], ARGV[7], ARGV[6] .. '|' .. ARGV[22] .. '|' .. ARGV[3])
				indexDue(ARGV[25], ARGV[16], '', ARGV[7], ARGV[6] .. '|' .. ARGV[22] .. '|' .. ARGV[3])
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
			'last', ARGV[1], 'active', ARGV[2], 'max', ARGV[3], 'expires', ARGV[7], 'details', ARGV[8])
		redis.call('PEXPIRE', KEYS[1], ARGV[6])
		redis.call('ZADD', KEYS[2], ARGV[4], ARGV[5])
		return 1
		""";
}

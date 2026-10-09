namespace Immediate.Jobs.Redis;

internal static class RedisScripts
{
	private const string FairQueueFunctions =
		"""
		local function fairDueKey(root, jobName, group)
			if group == '' then return root .. 'fair:ungrouped:' .. jobName end
			return root .. 'fair:due:' .. string.len(jobName) .. ':' .. jobName .. ':' .. group
		end
		local function indexDue(root, jobName, group, score, member)
			redis.call('ZADD', fairDueKey(root, jobName, group), score, member)
			if group ~= '' then redis.call('SADD', root .. 'fair:groups:' .. jobName, group) end
		end
		local function unindexDue(root, jobName, group, member)
			local key = fairDueKey(root, jobName, group)
			redis.call('ZREM', key, member)
			if group ~= '' and redis.call('ZCARD', key) == 0 then
				redis.call('SREM', root .. 'fair:groups:' .. jobName, group)
			end
		end
		local function changeActive(root, jobName, group, delta, id, lease)
			local key = root .. 'fair:active:' .. jobName
			if redis.call('HINCRBY', key, group, delta) <= 0 then redis.call('HDEL', key, group) end
			if delta > 0 then redis.call('ZADD', root .. 'active:job:' .. jobName, lease, id)
			else redis.call('ZREM', root .. 'active:job:' .. jobName, id) end
		end
		local function queueForJob(root, jobName)
			local metadata = redis.call('HGET', root .. 'definition-metadata', jobName)
			return metadata and cjson.decode(metadata).QueueName or nil
		end
		local function removeClearedQueueCursor(root, queue, group)
			if group == '' or not queue then return end
			local definitions = redis.call('HGETALL', root .. 'definition-metadata')
			for index = 1, #definitions, 2 do
				if cjson.decode(definitions[index + 1]).QueueName == queue then
					local name = definitions[index]
					if redis.call('HEXISTS', root .. 'fair:active:' .. name, group) == 1 then return end
					if redis.call('ZCARD', fairDueKey(root, name, group)) > 0 then return end
				end
			end
			redis.call('HDEL', root .. 'fair:cursor:' .. queue, group)
		end
		local function removeClearedCursor(root, jobName, group)
			removeClearedQueueCursor(root, queueForJob(root, jobName), group)
		end

		""";

	private const string DefinitionFunctions =
		"""
		local function createRecord(record, definitions, jobName)
			local metadata = redis.call('HGET', definitions, jobName)
			if not metadata then return nil end
			local definition = cjson.decode(metadata)
			local job = cjson.decode(record)
			job.JobName = definition.Name
			return cjson.encode(job), jobName
		end

		""";

	internal const string Enqueue =
		FairQueueFunctions + DefinitionFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 1 then
			return 0
		end
		local record, jobName = createRecord(ARGV[1], KEYS[4], ARGV[12])
		if not record then return -1 end

		redis.call('HSET', KEYS[1],
			'record', record, 'state', ARGV[2], 'due', ARGV[3], 'dueScore', ARGV[15],
			'created', ARGV[16], 'dueMember', ARGV[17],
			'attempt', ARGV[4], 'worker', ARGV[5], 'lease', ARGV[6],
			'error', ARGV[7], 'completed', ARGV[8],
			'executionTraceId', ARGV[9], 'executionSpanId', ARGV[10],
			'executionStartedAt', ARGV[11], 'jobName', ARGV[12], 'group', ARGV[18])
		redis.call('ZADD', KEYS[2], ARGV[13], ARGV[14])
		redis.call('SADD', KEYS[3], ARGV[14])
		if ARGV[2] == '2' or ARGV[2] == '3' then
			redis.call('ZADD', ARGV[19] .. 'due:' .. jobName, ARGV[15], ARGV[17])
			indexDue(ARGV[19], jobName, ARGV[18], ARGV[15], ARGV[17])
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
			local values = redis.call('HMGET', jobKey, 'worker', 'executionStartedAt', 'executionTraceId', 'executionSpanId', 'error')
			redis.call('ZADD', indexKey, attempt, attempt)
			redis.call('HSET', dataKey,
				attempt .. ':state', state, attempt .. ':worker', values[1] or '',
				attempt .. ':acquired', '', attempt .. ':started', values[2] or '',
				attempt .. ':completed', completed or '', attempt .. ':trace', values[3] or '',
				attempt .. ':span', values[4] or '', attempt .. ':error', state == '2' and (values[5] or '') or '',
				attempt .. ':synthetic', '1')
		end
		local nowScore = tonumber(ARGV[1])
		local leaseScore = tonumber(ARGV[2])
		local leaseTicks = ARGV[3]
		local worker = ARGV[4]
		local batchSize = tonumber(ARGV[5])
		local queueCount = tonumber(ARGV[6])
		local root = ARGV[7]
		local nowTicks = ARGV[8]
		local fair = ARGV[9] == '1'
		local threshold = tonumber(ARGV[10])
		local minimum = tonumber(ARGV[11])
		local roundRobin = ARGV[12] == '1'
		local queues = {}
		local eligible = {}
		local position = 13
		for index = 1, queueCount do
			local queue = { name = ARGV[position], capacity = tonumber(ARGV[position + 1]), jobs = {}, jobLimits = {} }
			local jobCount = tonumber(ARGV[position + 2])
			position = position + 3
			for jobIndex = 1, jobCount do
				local name, capacity = ARGV[position], tonumber(ARGV[position + 1])
				if queueForJob(root, name) == queue.name then
					queue.jobs[name] = capacity
					queue.jobLimits[name] = readLimits(ARGV, position + 2)
					eligible[name] = true
				end
				position = position + 9
			end
			queues[index] = queue
		end
		for _, id in ipairs(redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', nowScore)) do
			local key = root .. 'job:' .. id
			local values = redis.call('HMGET', key, 'state', 'jobName', 'attempt', 'lease', 'dueScore', 'dueMember', 'group')
			if values[1] == '4' and eligible[values[2]] then
				local attempt = tonumber(values[3] or '0')
				local indexKey, dataKey = root .. 'executions:index:' .. id, root .. 'executions:data:' .. id
				materialize(key, indexKey, dataKey, attempt, '0', '')
				if attempt > 0 then redis.call('HSET', dataKey, attempt .. ':state', '4', attempt .. ':completed', values[4] or '', attempt .. ':error', '') end
				redis.call('HSET', key, 'state', '3', 'worker', '', 'lease', '')
				redis.call('SREM', KEYS[2], id)
				redis.call('SADD', KEYS[3], id)
				redis.call('ZREM', KEYS[1], id)
				redis.call('ZADD', root .. 'due:' .. values[2], values[5], values[6])
				indexDue(root, values[2], values[7] or '', values[5], values[6])
				changeActive(root, values[2], values[7] or '', -1, id)
			elseif values[1] ~= '4' then redis.call('ZREM', KEYS[1], id) end
		end
		local acquired = {}
		local acquiredCount = 0
		local definitionStatuses = {}
		local function cachedDefinitionStatus(queue, name)
			local status = definitionStatuses[name]
			if status == nil then
				status = definitionState(root, name, queue.jobLimits[name], nowScore, nowTicks)[1]
				definitionStatuses[name] = status
			end
			return status
		end
		local function head(queue, group)
			local selected = nil
			for name, capacity in pairs(queue.jobs) do
				if capacity > 0 and cachedDefinitionStatus(queue, name) == 0 then
					local key = group == nil and (root .. 'due:' .. name) or fairDueKey(root, name, group)
					while true do
						local members = redis.call('ZRANGEBYSCORE', key, '-inf', nowScore, 'LIMIT', 0, 1)
						local member = members[1]
						if not member then break end
						local id = string.sub(member, 41)
						local values = redis.call('HMGET', root .. 'job:' .. id, 'state', 'dueMember', 'group', 'jobName')
						if (values[1] == '2' or values[1] == '3') and values[2] == member and values[4] == name then
							local candidate = { id = id, member = member, state = values[1], group = values[3] or '', jobName = name }
							if not selected or member < selected.member then selected = candidate end
							break
						end
						redis.call('ZREM', key, member)
					end
				end
			end
			return selected
		end
		local function claim(queue, candidate)
			local key = root .. 'job:' .. candidate.id
			local attempt = tonumber(redis.call('HGET', key, 'attempt') or '0')
			local indexKey, dataKey = root .. 'executions:index:' .. candidate.id, root .. 'executions:data:' .. candidate.id
			local error = redis.call('HGET', key, 'error') or ''
			materialize(key, indexKey, dataKey, attempt, error ~= '' and '2' or '4', '')
			attempt = attempt + 1
			redis.call('HSET', key, 'state', '4', 'attempt', attempt, 'worker', worker, 'lease', leaseTicks,
				'executionTraceId', '', 'executionSpanId', '', 'executionStartedAt', '')
			redis.call('ZREM', root .. 'due:' .. candidate.jobName, candidate.member)
			unindexDue(root, candidate.jobName, candidate.group, candidate.member)
			changeActive(root, candidate.jobName, candidate.group, 1, candidate.id, leaseScore)
			redis.call('ZADD', KEYS[1], leaseScore, candidate.id)
			redis.call('SREM', candidate.state == '2' and KEYS[4] or KEYS[3], candidate.id)
			redis.call('SADD', KEYS[2], candidate.id)
			redis.call('ZADD', indexKey, attempt, attempt)
			redis.call('HSET', dataKey, attempt .. ':state', '0', attempt .. ':worker', worker,
				attempt .. ':acquired', nowTicks, attempt .. ':started', '', attempt .. ':completed', '',
				attempt .. ':trace', '', attempt .. ':span', '', attempt .. ':error', '', attempt .. ':synthetic', '0')
			queue.jobs[candidate.jobName] = queue.jobs[candidate.jobName] - 1
			redis.call('ZADD', root .. 'definitions:leases:' .. candidate.jobName, leaseScore, candidate.id)
			recordDefinitionAcquisition(root, candidate.jobName, queue.jobLimits[candidate.jobName], nowTicks, candidate.id, attempt)
			definitionStatuses[candidate.jobName] = nil
			cachedDefinitionStatus(queue, candidate.jobName)
			table.insert(acquired, candidate.id)
			table.insert(acquired, queue.name)
			acquiredCount = acquiredCount + 1
		end
		local definitions = redis.call('HGETALL', root .. 'definition-metadata')
		for _, queue in ipairs(queues) do
			local remaining = math.min(queue.capacity, batchSize - acquiredCount)
			local groups, active, total, served, sequence = {}, {}, 0, {}, 0
			if fair then
				for index = 1, #definitions, 2 do
					local name = definitions[index]
					if cjson.decode(definitions[index + 1]).QueueName == queue.name then
						for _, group in ipairs(redis.call('SMEMBERS', root .. 'fair:groups:' .. name)) do groups[group] = true end
						for _, id in ipairs(redis.call('ZRANGEBYSCORE', root .. 'active:job:' .. name, '(' .. nowScore, '+inf')) do
							local group = redis.call('HGET', root .. 'job:' .. id, 'group') or ''
							active[group] = (active[group] or 0) + 1
							total = total + 1
						end
					end
				end
				local cursors = redis.call('HGETALL', root .. 'fair:cursor:' .. queue.name)
				for index = 1, #cursors, 2 do
					served[cursors[index]] = tonumber(cursors[index + 1])
					sequence = math.max(sequence, served[cursors[index]])
				end
			end
			local function before(left, right)
				local leftCount, rightCount = active[left.group] or 0, active[right.group] or 0
				local leftNoisy = left.group ~= '' and total > 0 and leftCount >= minimum and leftCount / total > threshold
				local rightNoisy = right.group ~= '' and total > 0 and rightCount >= minimum and rightCount / total > threshold
				if leftNoisy ~= rightNoisy then return not leftNoisy end
				if leftNoisy and leftCount ~= rightCount then return leftCount < rightCount end
				if roundRobin and (served[left.group] or 0) ~= (served[right.group] or 0) then return (served[left.group] or 0) < (served[right.group] or 0) end
				return left.member < right.member
			end
			while remaining > 0 do
				local candidate = head(queue, fair and '' or nil)
				if fair then
					for group in pairs(groups) do
						local grouped = head(queue, group)
						if grouped and (not candidate or before(grouped, candidate)) then candidate = grouped end
					end
				end
				if not candidate then break end
				claim(queue, candidate)
				remaining = remaining - 1
				active[candidate.group] = (active[candidate.group] or 0) + 1
				total = total + 1
				if fair and roundRobin and candidate.group ~= '' then
					sequence = sequence + 1
					served[candidate.group] = sequence
					redis.call('HSET', root .. 'fair:cursor:' .. queue.name, candidate.group, sequence)
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
		redis.call('ZADD', ARGV[6] .. 'active:job:' .. redis.call('HGET', KEYS[1], 'jobName'), ARGV[4], ARGV[5])
		return 1
		""";

	internal const string Complete =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'attempt',
			'executionStartedAt', 'executionTraceId', 'executionSpanId', 'jobName', 'group')
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
		changeActive(ARGV[6], values[7], values[8] or '', -1, ARGV[4])
		removeClearedCursor(ARGV[6], values[7], values[8] or '')
		return 1
		""";

	internal const string Fail =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'worker', 'jobName', 'created', 'attempt',
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
		changeActive(ARGV[10], values[3], group, -1, ARGV[3])
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
		local values = redis.call('HMGET', KEYS[1], 'state', 'jobName', 'created', 'group', 'record')
		if values[2] ~= ARGV[1] then return -1 end
		if values[1] ~= '1' then return -2 end
		if cjson.decode(values[5]).Payload == '' then return -3 end
		local state = tonumber(ARGV[3]) <= tonumber(ARGV[4]) and '3' or '2'
		local member = ARGV[2] .. '|' .. values[3] .. '|' .. ARGV[6]
		redis.call('HSET', KEYS[1], 'state', state, 'due', ARGV[2], 'dueScore', ARGV[3], 'dueMember', member)
		redis.call('SREM', ARGV[5] .. 'state:1', ARGV[6])
		redis.call('SADD', ARGV[5] .. 'state:' .. state, ARGV[6])
		redis.call('ZADD', ARGV[5] .. 'due:' .. values[2], ARGV[3], member)
		indexDue(ARGV[5], values[2], values[4] or '', ARGV[3], member)
		return 1
		""";

	private const string CancelFunctions = FairQueueFunctions +
		"""
		local function cancelJob(root, id, completedAt, completedScore, reason)
			if redis.call('EXISTS', root .. 'job:' .. id) == 0 then return 0 end
			local values = redis.call('HMGET', root .. 'job:' .. id, 'state', 'jobName', 'dueMember', 'attempt',
				'worker', 'executionStartedAt', 'executionTraceId', 'executionSpanId', 'group')
			local state = values[1]
			if state == '5' or state == '6' or state == '7' or state == '8' then return -1 end
			local attempt = tonumber(values[4] or '0')
			if state == '4' and attempt > 0 then
				local field = tostring(attempt) .. ':'
				if redis.call('HEXISTS', root .. 'executions:data:' .. id, field .. 'state') == 0 then
					redis.call('ZADD', root .. 'executions:index:' .. id, attempt, attempt)
					redis.call('HSET', root .. 'executions:data:' .. id,
						field .. 'worker', values[5] or '', field .. 'acquired', '',
						field .. 'started', values[6] or '', field .. 'trace', values[7] or '',
						field .. 'span', values[8] or '', field .. 'synthetic', '1')
				end
				redis.call('HSET', root .. 'executions:data:' .. id,
					field .. 'state', '3', field .. 'completed', completedAt, field .. 'error', reason)
			end
			redis.call('HSET', root .. 'job:' .. id,
				'state', '7', 'worker', '', 'lease', '', 'error', '', 'completed', completedAt, 'cancellationReason', reason)
			redis.call('SREM', root .. 'state:' .. state, id)
			redis.call('SADD', root .. 'state:7', id)
			redis.call('ZREM', root .. 'leases', id)
			if values[3] then redis.call('ZREM', root .. 'due:' .. values[2], values[3]) end
			redis.call('ZADD', root .. 'completed:7', completedScore, id)
			local group = values[9] or ''
			if state == '4' then changeActive(root, values[2], group, -1, id) end
			if values[3] then unindexDue(root, values[2], group, values[3]) end
			removeClearedCursor(root, values[2], group)
			return 1
		end

		""";

	internal const string Cancel = CancelFunctions +
		"""
		return cancelJob(ARGV[4], ARGV[1], ARGV[2], ARGV[3], '')
		""";

	internal const string Retry =
		FairQueueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local values = redis.call('HMGET', KEYS[1], 'state', 'jobName', 'created', 'dueMember',
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

	private const string RecurringDueFunctions =
		"""
		local function removeRecurringDue(root, dueKey, jobName, member)
			if not member then return end
			redis.call('ZREM', dueKey, member)
			if jobName then redis.call('ZREM', root .. 'recurring:due:job:' .. jobName, member) end
		end
		local function addRecurringDue(root, dueKey, jobName, score, member)
			redis.call('ZADD', dueKey, score, member)
			redis.call('ZADD', root .. 'recurring:due:job:' .. jobName, score, member)
		end

		""";

	internal const string MergeDefinitionCatalog =
		CancelFunctions + RecurringDueFunctions +
		"""
		if (redis.call('GET', KEYS[2]) or '0') ~= ARGV[1] then return false end
		local changes = cjson.decode(ARGV[3])
		local reports, removed = {}, {}
		for _, name in ipairs(changes.RemovedDefinitions) do
			removed[name] = { jobs = {}, schedules = {} }
		end
		if #changes.RemovedDefinitions > 0 then
			for _, id in ipairs(redis.call('ZRANGE', ARGV[2] .. 'jobs', 0, -1)) do
				local name = redis.call('HGET', ARGV[2] .. 'job:' .. id, 'jobName')
				if name and removed[name] then table.insert(removed[name].jobs, id) end
			end
			for _, name in ipairs(redis.call('SMEMBERS', KEYS[3])) do
				local jobName = redis.call('HGET', ARGV[2] .. 'recurring:' .. name, 'jobName')
				if jobName and removed[jobName] then table.insert(removed[jobName].schedules, name) end
			end
		end
		for _, name in ipairs(changes.RemovedDefinitions) do
			local jobCount, scheduleCount = 0, 0
			for _, id in ipairs(removed[name].jobs) do
				if cancelJob(ARGV[2], id, ARGV[4], tonumber(ARGV[5]), ARGV[6]) == 1 then jobCount = jobCount + 1 end
			end
			for _, scheduleName in ipairs(removed[name].schedules) do
				local key = ARGV[2] .. 'recurring:' .. scheduleName
				local values = redis.call('HMGET', key, 'code', 'cancellationReason', 'dueMember')
				if values[1] ~= '1' and (not values[2] or values[2] == '') then
					redis.call('HSET', key, 'paused', '1', 'cancellationReason', ARGV[6])
					removeRecurringDue(ARGV[2], KEYS[4], name, values[3])
					scheduleCount = scheduleCount + 1
				end
			end
			reports[#reports + 1] = { name, jobCount, scheduleCount }
			redis.call('HDEL', KEYS[1], name)
		end
		local previousQueues = {}
		for _, definition in ipairs(changes.Definitions) do
			local previous = redis.call('HGET', KEYS[1], definition.Name)
			if previous then previousQueues[cjson.decode(previous).QueueName] = true end
			redis.call('HSET', KEYS[1], definition.Name, definition.Metadata)
		end
		for queue in pairs(previousQueues) do
			for _, group in ipairs(redis.call('HKEYS', ARGV[2] .. 'fair:cursor:' .. queue)) do
				removeClearedQueueCursor(ARGV[2], queue, group)
			end
		end
		for _, name in ipairs(changes.RemovedSchedules) do
			local key = ARGV[2] .. 'recurring:' .. name
			if redis.call('HGET', key, 'code') == '1' then
				local due = redis.call('HMGET', key, 'dueMember', 'jobName')
				removeRecurringDue(ARGV[2], KEYS[4], due[2], due[1])
				redis.call('DEL', key)
				redis.call('SREM', KEYS[3], name)
			end
		end
		for _, schedule in ipairs(changes.Schedules) do
			local key = ARGV[2] .. 'recurring:' .. schedule.Name
			local paused, last, next = schedule.Paused, schedule.Last, schedule.Next
			local dueScore, dueMember = schedule.DueScore, schedule.DueMember
			local current = redis.call('HMGET', key, 'paused', 'last', 'next', 'dueScore', 'dueMember', 'cron', 'timeZone', 'jobName')
			paused = current[1] or paused
			last = current[2] or last
			removeRecurringDue(ARGV[2], KEYS[4], current[8], current[5])
			if current[6] == schedule.Cron and current[7] == schedule.TimeZone then
				next = current[3] or next
				dueScore = current[4] or dueScore
				dueMember = current[5] or dueMember
			end
			redis.call('HSET', key, 'record', schedule.Record, 'code', '1', 'paused', paused,
				'next', next, 'last', last, 'dueScore', dueScore, 'dueMember', dueMember,
				'cron', schedule.Cron, 'timeZone', schedule.TimeZone, 'jobName', schedule.JobName)
			redis.call('SADD', KEYS[3], schedule.Name)
			if paused ~= '1' then addRecurringDue(ARGV[2], KEYS[4], schedule.JobName, dueScore, dueMember) end
		end
		redis.call('INCR', KEYS[2])
		return reports
		""";

	internal const string GetDueRecurringForJobs =
		"""
		local limit = tonumber(ARGV[3])
		local result, jobs, seen = {}, {}, {}
		if limit <= 0 then return result end
		for index = 4, #ARGV do
			local jobName = ARGV[index]
			if not jobs[jobName] then
				jobs[jobName] = true
				local key = ARGV[1] .. 'recurring:due:job:' .. jobName
				local members = redis.call('ZRANGEBYSCORE', key, '-inf', ARGV[2], 'LIMIT', 0, limit)
				for _, member in ipairs(members) do
					local name = string.sub(member, 21)
					local scheduleKey = ARGV[1] .. 'recurring:' .. name
					local current = redis.call('HMGET', scheduleKey, 'paused', 'dueMember', 'jobName')
					if current[1] ~= '1' and current[2] == member and current[3] == jobName and not seen[member] then
						seen[member] = true
						table.insert(result, member)
					end
				end
			end
		end
		table.sort(result)
		while #result > limit do table.remove(result) end
		return result
		""";

	internal const string UpsertRecurring =
		RecurringDueFunctions + DefinitionFunctions +
		"""
		local record = ARGV[1]
		local canonical = createRecord(record, KEYS[4], ARGV[11])
		if canonical then record = canonical
		end

		local exists = redis.call('EXISTS', KEYS[1]) == 1
		local paused = ARGV[3]
		local last = ARGV[5]
		local next = ARGV[4]
		local dueScore = ARGV[7]
		local dueMember = ARGV[8]
		local cancellationReason = cjson.decode(ARGV[1]).CancellationReason
		if not cancellationReason or cancellationReason == cjson.null then
			cancellationReason = redis.call('HGET', KEYS[1], 'cancellationReason') or ''
		end
		if exists then
			local current = redis.call('HMGET', KEYS[1], 'code', 'paused', 'last', 'dueMember', 'jobName')
			if current[1] == '1' and ARGV[2] == '0' then return -1 end
			paused = current[2] or paused
			last = current[3] or last
			removeRecurringDue(ARGV[12], KEYS[3], current[5], current[4])

		end
		if cancellationReason ~= '' then paused = '1' end
		redis.call('HSET', KEYS[1],
			'record', record, 'code', ARGV[2], 'paused', paused,
			'next', next, 'last', last, 'dueScore', dueScore, 'dueMember', dueMember,
			'cron', ARGV[9], 'timeZone', ARGV[10], 'jobName', ARGV[11],
			'cancellationReason', cancellationReason)
		redis.call('SADD', KEYS[2], ARGV[6])
		if paused == '1' then
			removeRecurringDue(ARGV[12], KEYS[3], ARGV[11], dueMember)
		else
			addRecurringDue(ARGV[12], KEYS[3], ARGV[11], dueScore, dueMember)
		end
		return 1
		""";

	internal const string RemoveRecurring =
		RecurringDueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		if redis.call('HGET', KEYS[1], 'code') == '1' then return -1 end
		local due = redis.call('HMGET', KEYS[1], 'dueMember', 'jobName')
		removeRecurringDue(ARGV[2], KEYS[3], due[2], due[1])
		redis.call('DEL', KEYS[1])
		redis.call('SREM', KEYS[2], ARGV[1])
		return 1
		""";

	internal const string SetRecurringPaused =
		RecurringDueFunctions +
		"""
		if ARGV[1] == '0' and (redis.call('HGET', KEYS[1], 'cancellationReason') or '') ~= '' then return -1 end
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		redis.call('HSET', KEYS[1], 'paused', ARGV[1])
		local due = redis.call('HMGET', KEYS[1], 'dueScore', 'dueMember', 'jobName')
		if ARGV[1] == '1' then
			removeRecurringDue(ARGV[2], KEYS[2], due[3], due[2])
		elseif due[1] and due[2] then
			addRecurringDue(ARGV[2], KEYS[2], due[3], due[1], due[2])
		end
		return 1
		""";

	internal const string MaterializeRecurring =
		FairQueueFunctions + RecurringDueFunctions +
		"""
		if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
		local schedule = redis.call('HMGET', KEYS[1], 'next', 'paused', 'dueMember', 'jobName')
		if schedule[1] ~= ARGV[1] or schedule[2] == '1' then return 0 end
		local previousDueMember = schedule[3]
		if not previousDueMember or previousDueMember ~= ARGV[1] .. '|' .. ARGV[20] then return 0 end
		local previousDueScore = redis.call('ZSCORE', KEYS[7], previousDueMember)
		if not previousDueScore or tonumber(previousDueScore) > tonumber(ARGV[23]) then return 0 end
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
				'created', ARGV[21], 'dueMember', ARGV[6] .. '|' .. ARGV[21] .. '|' .. ARGV[3],
				'recurringKey', ARGV[2],
				'attempt', ARGV[8], 'worker', ARGV[9], 'lease', ARGV[10],
				'error', ARGV[11], 'completed', ARGV[12],
				'executionTraceId', ARGV[13], 'executionSpanId', ARGV[14],
				'executionStartedAt', ARGV[15], 'jobName', ARGV[16], 'group', '')
			redis.call('ZADD', KEYS[4], ARGV[17], ARGV[3])
			redis.call('SADD', KEYS[5], ARGV[3])
			if ARGV[5] == '2' or ARGV[5] == '3' then
				redis.call('ZADD', KEYS[6], ARGV[7], ARGV[6] .. '|' .. ARGV[21] .. '|' .. ARGV[3])
				indexDue(ARGV[24], ARGV[16], '', ARGV[7], ARGV[6] .. '|' .. ARGV[21] .. '|' .. ARGV[3])
			elseif ARGV[5] == '7' or ARGV[5] == '8' then
				redis.call('ZADD', KEYS[8], ARGV[22], ARGV[3])
			end
		end
		local nextDueMember = ARGV[18] .. '|' .. ARGV[20]
		redis.call('HSET', KEYS[1],
			'last', ARGV[1], 'next', ARGV[18], 'dueScore', ARGV[19], 'dueMember', nextDueMember)
		removeRecurringDue(ARGV[24], KEYS[7], schedule[4], previousDueMember)
		addRecurringDue(ARGV[24], KEYS[7], schedule[4], ARGV[19], nextDueMember)
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

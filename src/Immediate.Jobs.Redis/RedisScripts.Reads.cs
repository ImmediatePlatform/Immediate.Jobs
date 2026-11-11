namespace Immediate.Jobs.Redis;

internal static partial class RedisScripts
{
	// Keep this projection in the same order as ToJobRecord. False values preserve missing fields in RESP arrays.
	private const string JobReadFunctions =
		"""
		local queues = {}
		local function readJob(root, key, queue)
			local values = redis.call('HMGET', key, 'record', 'state', 'due', 'attempt', 'worker', 'lease',
				'error', 'completed', 'executionTraceId', 'executionSpanId', 'executionStartedAt', 'cancellationReason', 'jobName')
			local name = values[13]
			if queue == nil and name then
				queue = queues[name]
				if queue == nil then
					local metadata = redis.call('HGET', root .. 'definition-metadata', name)
					queue = metadata and cjson.decode(metadata).QueueName or false
					queues[name] = queue
				end
			end
			values[13] = queue or false
			return values
		end

		""";

	internal const string ReadJob = JobReadFunctions +
		"""
		local values = readJob(ARGV[1], KEYS[2])
		return values[1] and { values } or {}
		""";

	// Bound server work to one candidate window even when the query is selective.
	internal const string ReadJobPage = JobReadFunctions +
		"""
		local ids = redis.call('ZREVRANGE', KEYS[1], ARGV[2], ARGV[3])
		local result = {}
		local filtered = ARGV[4] ~= '' or ARGV[5] ~= '' or ARGV[6] ~= ''
		for _, id in ipairs(ids) do
			local key = ARGV[1] .. 'job:' .. id
			local matches = true
			if filtered then
				local filter = redis.call('HMGET', key, 'state', 'jobName', 'created')
				matches = (ARGV[4] == '' or filter[1] == ARGV[4])
					and (ARGV[5] == '' or filter[2] == ARGV[5])
					and (ARGV[6] == '' or (filter[3] and filter[3] < ARGV[6]))
			end
			if matches then
				local values = readJob(ARGV[1], key)
				if values[1] then result[#result + 1] = values end
			end
		end
		return { #ids, result }
		""";

	internal const string ReadJobExecutions = JobReadFunctions +
		"""
		local current = redis.call('HMGET', KEYS[1], 'state', 'attempt')
		if not current[1] then return {} end
		local job = false
		local skip, take, synthetic = tonumber(ARGV[2]), tonumber(ARGV[3]), 0
		local latest = tonumber(current[2])
		if latest > 0 and (ARGV[4] == '' or tonumber(ARGV[4]) == latest)
			and redis.call('HEXISTS', KEYS[3], latest .. ':state') == 0 then
			if skip == 0 then
				synthetic = 1; take = take - 1
				job = readJob(ARGV[1], KEYS[1], false)
			else skip = skip - 1 end
		end
		local executions = {}
		if take > 0 then
			local attempts = {}
			if ARGV[4] == '' then
				attempts = redis.call('ZREVRANGE', KEYS[2], skip, skip + take - 1)
			elseif skip == 0 then attempts = { ARGV[4] } end
			for _, attempt in ipairs(attempts) do
				local fields = {}
				for index = 5, #ARGV do fields[#fields + 1] = attempt .. ':' .. ARGV[index] end
				local values = redis.call('HMGET', KEYS[3], unpack(fields))
				if values[1] then
					table.insert(values, 1, attempt)
					executions[#executions + 1] = values
				end
			end
		end
		return { job, synthetic, executions }
		""";

}

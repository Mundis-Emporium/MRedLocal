local root = assert(os.getenv('VORP_TEST_ROOT'), 'VORP_TEST_ROOT must point to installed fixture') .. '/'
local owner, license, alias = 'steam:abcdef1234567890', 'license:abcdef1234567890', 'localtest:secondary'
local players, rows, charCounts, handlers, timers, dropped, saved, updates, inserts, triggered
local convars

local function assertEqual(actual, expected, label)
    assert(actual == expected, label .. ': expected ' .. tostring(expected) .. ', got ' .. tostring(actual))
end

local function start(settings)
    players, rows, charCounts, handlers, timers, dropped, saved, updates, inserts, triggered = {}, {}, {}, {}, {}, {}, {}, {}, {}, {}
    convars = settings or {}
    source = nil
    Config = {
        SavePlayersStatus = false, SaveDiscordId = false, ReportCrash = false,
        DeleteFromUsersTable = false, MaxCharacters = 5, MaxCharacterJobs = 5,
        initGroup = 'user', savePlayersTimer = 30,
    }
    Lang = 'en'
    Translation = { en = { MessageOfSystem = { PlayerJoinLeave = {} }, Notify = { banned3 = 'banned' } } }
    Logs = { EnableWebhookJoinleave = false }
    GlobalState = { PlayersInSession = 0 }
    WhiteListedUsers = {}
    Whitelist = { Functions = { GetUserId = function(identifier)
        if identifier == owner then return 42 end
    end } }
    MySQL = {
        single = { await = function(_, args) return rows[args[1]] end },
        scalar = { await = function(query, args)
            if query:find('FROM characters', 1, true) then return charCounts[args[1]] or 0 end
            return 1
        end },
        update = setmetatable({ await = function(query, args)
            assert(query:find('INSERT INTO users', 1, true), 'unexpected awaited update')
            inserts[#inserts + 1] = { query = query, args = args }
            rows[args[1]] = { group = args[2], warnings = args[3], char = args[6], max_jobs = args[7] }
            return 1
        end }, { __call = function(_, query, args)
            updates[#updates + 1] = { query = query, args = args }
        end }),
        insert = function() error('unexpected account insert') end,
    }
    function GetConvar(name, default) return convars[name] or default end
    function GetPlayerIdentifierByType(src, kind)
        local player = players[tonumber(src)]
        return player and player[kind]
    end
    function GetPlayerEndpoint(src)
        local player = players[tonumber(src)]
        return player and player.endpoint
    end
    function GetPlayerName(src) return 'player' .. tostring(src) end
    function GetDiscordID() return '' end
    function Player(src)
        local player = players[tonumber(src)]
        return { state = { set = function(_, name, value) player[name] = value end } }
    end
    function AddEventHandler(name, fn) handlers[name] = fn end
    function RegisterNetEvent(name, fn) if fn then handlers[name] = fn end end
    function RegisterServerEvent(name, fn) if fn then handlers[name] = fn end end
    function CreateThread() end
    function exports() end
    function SetTimeout(delay, fn) timers[#timers + 1] = { delay = delay, fn = fn } end
    function DropPlayer(src, reason) dropped[tonumber(src)] = reason end
    function TriggerClientEvent() end
    function TriggerEvent(name, src) triggered[#triggered + 1] = { name = name, source = src } end
    function SetPlayerRoutingBucket() end
    function User(src, identifier, group, warnings, realLicense, charperm)
        local user = { source = src, identifier = identifier, license = realLicense,
            usedCharacterId = -1, _charperm = charperm, sourceChanges = 0 }
        function user.LoadCharacters() end
        function user.GetUsedCharacter()
            if user.usedCharacterId ~= -1 then return {} end
        end
        function user.SaveUser() saved[#saved + 1] = identifier end
        function user.GetUser() return { source = src, getIdentifier = function() return identifier end } end
        function user.Source(newSource)
            user.source = newSource
            user.sourceChanges = user.sourceChanges + 1
        end
        function user.Numofcharacters()
            if identifier == alias then return charCounts[alias] or 0 end
            return 1
        end
        return user
    end
    dofile(root .. 'server/local_sessions/identity.lua')
    dofile(root .. 'server/loadusers.lua')
    dofile(root .. 'server/apicontroller.lua')
    dofile(root .. 'server/old_api.lua')
    dofile(root .. 'server/loadcharacter.lua')
    dofile(root .. 'server/bans.lua')
end

local function connect(src, steam, lic, endpoint)
    players[src] = { steam = steam, license = lic, endpoint = endpoint }
    source = src
    handlers.playerJoining()
end

local function disconnect(src)
    source = src
    handlers.playerDropped('quit')
end

local function flush(delay)
    local pending = timers
    timers = {}
    for _, timer in ipairs(pending) do
        if timer.delay == delay then timer.fn() else timers[#timers + 1] = timer end
    end
end

start({})
rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
connect(1, owner, license, '127.0.0.1')
assertEqual(LocalPeerIdentity.Resolve(1), owner, 'disabled resolver')
assertEqual(_users[owner].source, 1, 'disabled user')

start({ redm_local_mode = 'two_player', redm_local_owner_steam = owner,
    redm_local_owner_license = license, redm_local_alias = alias })
rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
rows[alias] = { group = 'user', warnings = 0, char = 1, max_jobs = 5 }
rows['steam:other'] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
charCounts[alias] = 1
connect(1, owner, license, '127.0.0.1')
connect(2, owner, license, '::1')
connect(6, 'steam:other', 'license:other', '198.51.100.6')
assertEqual(LocalPeerIdentity.Resolve(1), owner, 'primary resolver')
assertEqual(LocalPeerIdentity.Resolve(2), alias, 'peer resolver')
assertEqual(_users[owner].source, 1, 'primary user source')
assertEqual(_users[alias].source, 2, 'alias user source')
assertEqual(_users['steam:other'].source, 6, 'unrelated user unchanged')
assertEqual(_users[alias].license, license, 'real license retained')
assertEqual(CoreFunctions.getUser(1).getIdentifier(), owner, 'primary Core API')
assertEqual(CoreFunctions.getUser(2).getIdentifier(), alias, 'peer Core API')
assertEqual(LocalPeerIdentity.IsAdditionalOwnerConnection(2), true, 'secondary Discord guard')

connect(3, owner, license, '198.51.100.2')
assert(dropped[3], 'remote duplicate must be dropped')
assertEqual(LocalPeerIdentity.Resolve(3), nil, 'remote duplicate resolver')
connect(4, owner, 'license:other', '127.0.0.1')
assert(dropped[4], 'wrong license must be dropped')
connect(5, owner, license, '127.0.0.1')
assert(dropped[5], 'third owner session must be dropped')
disconnect(3)
disconnect(4)
disconnect(5)
assertEqual(_users[owner].source, 1, 'rejected join preserves primary')
assertEqual(_users[alias].source, 2, 'rejected join preserves peer')

_users[owner].usedCharacterId = 10
_users[alias].usedCharacterId = 20
GlobalState.PlayersInSession = 2
disconnect(2)
assertEqual(saved[#saved], alias, 'peer save')
assertEqual(LocalPeerIdentity.Resolve(2), alias, 'drop handler resolver retained')
assertEqual(CoreFunctions.getUser(2).getIdentifier(), alias, 'peer visible to other drop handlers')
assertEqual(GlobalState.PlayersInSession, 1, 'peer count decremented')
local originalAwait = MySQL.single.await
local oldPeer = _users[alias]
MySQL.single.await = function(query, args)
    if args[1] == alias then return coroutine.yield() end
    return originalAwait(query, args)
end
local reconnect = coroutine.create(function()
    connect(2, owner, license, '127.0.0.1')
end)
assert(coroutine.resume(reconnect), 'reconnect coroutine starts')
assertEqual(coroutine.status(reconnect), 'suspended', 'reconnect waits for database')
assertEqual(CoreFunctions.getUser(2), nil, 'old Core user hidden while loading')
assertEqual(LocalPeerIdentity.LoadedUser(2), nil, 'old session user hidden while loading')
source = 2
handlers['vorp:playerSpawn']()
handlers['vorp:SaveHealth'](100, 50)
handlers['vorp:HealthCached'](100, 50, 100, 50)
handlers['vorp:ImDead'](true)
handlers['vorp:GetValues']()
local staleCallback = false
handlers['vorp:getCharacter'](2, function() staleCallback = true end)
handlers['vorpwarns:addtodb'](true, 2, 1)
assertEqual(oldPeer.sourceChanges, 0, 'stale spawn cannot rebind old user')
assertEqual(_healthData[alias], nil, 'stale health cannot overwrite cache')
assertEqual(staleCallback, false, 'legacy API cannot return old character')
assert(coroutine.resume(reconnect, rows[alias]), 'reconnect coroutine completes')
MySQL.single.await = originalAwait
local replacement = _users[alias]
assertEqual(CoreFunctions.getUser(2).getIdentifier(), alias, 'new Core user after load')
flush(0)
flush(6000)
assertEqual(LocalPeerIdentity.Resolve(2), alias, 'source reuse retained')
assertEqual(_users[alias], replacement, 'old timer preserves new session')
assertEqual(_users[owner].source, 1, 'source reuse preserves primary')
WhiteListedUsers[42] = true
disconnect(2)
assertEqual(WhiteListedUsers[42], true, 'primary keeps whitelist after peer drop')
disconnect(1)
assertEqual(WhiteListedUsers[42], nil, 'final primary drop clears whitelist')
flush(0)
assertEqual(LocalPeerIdentity.Resolve(1), nil, 'primary tombstone blocks raw fallback')
assertEqual(LocalPeerIdentity.Resolve(2), nil, 'peer tombstone blocks raw fallback')
assertEqual(CoreFunctions.getUser(1), nil, 'disconnected primary Core lookup')

start({ redm_local_mode = 'two_player', redm_local_owner_steam = owner,
    redm_local_owner_license = license, redm_local_alias = alias })
rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
connect(1, owner, license, '127.0.0.1')
connect(2, owner, license, '127.0.0.1')
assertEqual(dropped[2], nil, 'missing alias account created')
assertEqual(rows[alias].group, 'user', 'alias group is unprivileged')
assertEqual(rows[alias].char, 1, 'alias permits one character')
assertEqual(rows[alias].max_jobs, 1, 'alias max jobs limited')
assertEqual(inserts[1].args[1], alias, 'alias insert identifier')
assertEqual(CoreFunctions.getUser(2).getIdentifier(), alias, 'new alias Core API')
source = 2
handlers['vorp:playerSpawn']()
assertEqual(triggered[#triggered].name, 'vorp_CreateNewCharacter', 'empty alias starts normal creator')
disconnect(2)
charCounts[alias] = 1
connect(2, owner, license, '127.0.0.1')
assertEqual(dropped[2], nil, 'single alias character accepted')
assertEqual(_users[owner].source, 1, 'alias creation preserves primary')

start({ redm_local_mode = 'two_player', redm_local_owner_steam = owner,
    redm_local_owner_license = license, redm_local_alias = alias })
rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
rows[alias] = { group = 'user', warnings = 0, char = 1, max_jobs = 5 }
charCounts[alias] = 1
connect(1, owner, license, '127.0.0.1')
connect(2, owner, license, '127.0.0.1')
WhiteListedUsers[42] = true
disconnect(1)
assertEqual(WhiteListedUsers[42], true, 'peer keeps real owner whitelist after primary drop')
disconnect(2)
assertEqual(WhiteListedUsers[42], nil, 'final peer drop clears real owner whitelist')

start({ redm_local_mode = 'two_player', redm_local_owner_steam = owner,
    redm_local_owner_license = license, redm_local_alias = alias })
rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
rows[alias] = { group = 'user', warnings = 0, char = 1, max_jobs = 5 }
charCounts[alias] = 1
connect(1, owner, license, '127.0.0.1')
connect(2, owner, license, '127.0.0.1')
handlers['vorpbans:addtodb'](true, alias, 0)
assertEqual(dropped[1], 'banned', 'alias ban drops primary')
assertEqual(dropped[2], 'banned', 'alias ban drops peer')
assertEqual(updates[#updates].args['@identifier'], owner, 'alias ban persists against real owner')

start({ redm_local_mode = 'two_player' })
rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
rows['steam:deadbeef'] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
connect(9, 'steam:deadbeef', 'license:deadbeef', '198.51.100.9')
connect(1, owner, license, '127.0.0.1')
connect(2, owner, license, '[::1]')
assertEqual(CoreFunctions.getUser(1).getIdentifier(), owner, 'automatic primary')
assertEqual(CoreFunctions.getUser(2).getIdentifier(), alias, 'automatic peer')
assertEqual(CoreFunctions.getUser(9).getIdentifier(), 'steam:deadbeef', 'remote cannot bind automatic owner')
assertEqual(GetPlayerIdentifierByType(2, 'steam'), owner, 'native Steam stays real')
assertEqual(GetPlayerIdentifierByType(2, 'license'), license, 'native license stays real')
disconnect(1); disconnect(2)
connect(3, 'steam:deadbeef', 'license:deadbeef', '127.0.0.1')
connect(4, owner, license, '127.0.0.1')
connect(5, owner, license, '::1')
assertEqual(LocalPeerIdentity.Resolve(3), 'steam:deadbeef', 'owner remains fixed after both disconnect')
assertEqual(LocalPeerIdentity.Resolve(4), owner, 'fixed owner reconnects')
assertEqual(LocalPeerIdentity.Resolve(5), alias, 'fixed owner peer reconnects')

for _, settings in ipairs({
    { redm_local_mode = 'two_player', redm_local_owner_steam = owner },
    { redm_local_mode = 'two_player', redm_local_owner_license = license },
    { redm_local_mode = 'two_player', redm_local_owner_steam = 'invalid', redm_local_owner_license = license },
    { redm_local_mode = 'two_player', redm_local_alias = 'steam:abcdef' },
}) do
    start(settings)
    rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
    connect(1, owner, license, '127.0.0.1')
    assert(dropped[1], 'invalid or partial local owner settings rejected')
    assertEqual(LocalPeerIdentity.Resolve(1), nil, 'invalid settings never expose profile')
end
start({ redm_local_mode = 'two_player', redm_local_owner_steam = owner, redm_local_owner_license = license })
connect(1, owner, license, '198.51.100.1')
assert(dropped[1], 'explicit primary must use loopback')

local inventoryRoot = os.getenv('VORP_INVENTORY_ROOT')
if inventoryRoot then
    start({ redm_local_mode = 'two_player' })
    rows[owner] = { group = 'user', warnings = 0, char = 5, max_jobs = 5 }
    connect(1, owner, license, '127.0.0.1')
    connect(2, owner, license, '127.0.0.1')
    for identifier, user in pairs(_users) do
        user.GetUser = function() return {
            getIdentifier = function() return identifier end,
            getUsedCharacter = { identifier = identifier, charIdentifier = identifier == owner and 101 or 202 }
        } end
    end
    TranslationInv = { Langs = { en = {} } }
    exports = setmetatable({ vorp_core = { GetCore = function() return CoreFunctions end } }, { __call = function() end })
    local file = assert(io.open(inventoryRoot .. '/server/services/inventoryApiService.lua', 'rb'))
    local text = file:read('*a'); file:close()
    local count
    text, count = text:gsub('item%.metadata%?%.([%w_]+)', function(field)
        return '(item.metadata and item.metadata.' .. field .. ')'
    end)
    assertEqual(count, 9, 'inventory Cfx metadata syntax')
    text, count = text:gsub('Core%.getUser%(_source%)%?%.getUsedCharacter',
        '(Core.getUser(_source) and Core.getUser(_source).getUsedCharacter)')
    assertEqual(count, 1, 'inventory Cfx user syntax')
    assert(load(text, '@inventoryApiService.lua'))()
    local function item(amount)
        local values = { Id = amount, Label = 'Apple', Name = 'apple', Desc = '', Metadata = {}, Type = 'item',
            Count = amount, Limit = 100, CanUse = true, Group = 1, Weight = 1, Percentage = 100, MaxDegradation = 0 }
        local result = { metadata = {} }
        for name, value in pairs(values) do result['get' .. name] = function() return value end end
        return result
    end
    UsersInventories.default[owner] = { item(3) }
    UsersInventories.default[alias] = { item(7) }
    assertEqual(InventoryAPI.getInventory(1)[1].count, 3, 'actual inventory API primary isolation')
    assertEqual(InventoryAPI.getInventory(2)[1].count, 7, 'actual inventory API peer isolation')
    disconnect(1)
    flush(0)
    assertEqual(InventoryAPI.getInventory(1), nil, 'dropped primary inventory inaccessible')
    assertEqual(InventoryAPI.getInventory(2)[1].count, 7, 'primary disconnect preserves peer inventory')
end

print('local peer identity tests passed')

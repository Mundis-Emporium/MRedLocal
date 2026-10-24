local root = assert(os.getenv('RSG_TEST_ROOT')) .. '/'
local owner, license, alias = 'steam:abcdef1234567890', 'license:abcdef1234567890', 'localtest:secondary'
local players, rows, inventoryRows, handlers, dropped, writes, convars, onQuery, onWait
local nextCitizen
local function eq(actual, expected, label)
    assert(actual == expected, (label or '') .. ': expected ' .. tostring(expected) .. ', got ' .. tostring(actual))
end
local function clone(value)
    if type(value) ~= 'table' then return value end
    local result = {}; for k, v in pairs(value) do result[k] = clone(v) end; return result
end
local function loadCfx(relative)
    local file = assert(io.open(root .. relative, 'rb'))
    local text = file:read('*a'); file:close()
    -- Only normalize unrelated Cfx syntax for the stock Lua 5.4 test runtime.
    local count
    if relative == 'rsg-core/server/functions.lua' then
        text, count = text:gsub('count %+= 1', 'count = count + 1'); eq(count, 2, 'Cfx count syntax')
    elseif relative == 'rsg-inventory/server/exports.lua' then
        text, count = text:gsub('`WEAPON_UNARMED`', '0'); eq(count, 1, 'Cfx hash syntax outside tested paths')
    end
    assert(load(text, '@' .. root .. relative))()
end
local function start(settings)
    players, rows, inventoryRows, handlers, dropped, writes = {}, {}, {}, {}, {}, {}
    onQuery, onWait, nextCitizen = nil, nil, 0
    convars = settings or { redm_local_mode = 'two_player', redm_local_owner_steam = owner,
        redm_local_owner_license = license }
    RSGCore = { Config = { Money = { PayCheckTimeOut = 10, EnableMoneyItems = true }, Server = { CheckDuplicateLicense = true }, Player = {
        PlayerDefaults = { citizenid = function() nextCitizen = nextCitizen + 1; return 'NEW' .. nextCitizen end,
            metadata = {}, items = {}, money = { cash = 0, bloodmoney = 0 }, job = {}, gang = {}, cid = 1, charinfo = {}, weight = 100, slots = 10 }
    } }, Shared = { Jobs = {}, Gangs = {}, Items = { apple = { name = 'apple', label = 'Apple' } }, StarterItems = {} },
        ServerCallbacks = {}, Commands = { Add = function() end, Refresh = function() end },
        ShowSuccess = function() end, ShowError = function() end }
    Config = { StarterHorse = false }
    Lang = { t = function(_, key) return key end }
    json = { decode = function(v) if v == '[]' then return {} end; return clone(v) end,
        encode = function(v) return clone(v) end }
    function GetConvar(name, default) return convars[name] or default end
    function GetCurrentResourceName() return 'rsg-core' end
    function GetPlayerIdentifierByType(src, kind) return players[tonumber(src)] and players[tonumber(src)][kind] end
    function GetPlayerIdentifiers(src)
        local p = players[tonumber(src)]; return p and { p.steam, p.license } or {}
    end
    function GetPlayerEndpoint(src) return players[tonumber(src)] and players[tonumber(src)].endpoint end
    function GetPlayerName(src) return 'player' .. tostring(src) end
    function GetPlayers() local list = {}; for src in pairs(players) do list[#list + 1] = tostring(src) end; return list end
    function GetPlayerPed(src) return src end
    function GetEntityCoords() return {} end
    function SetPlayerRoutingBucket(src, bucket) players[src].bucket = bucket end
    function Player(src) return { state = players[src].state } end
    function GetResourceState() return 'started' end
    function DropPlayer(src, reason) dropped[src] = reason end
    function AddEventHandler(name, fn) handlers[name] = handlers[name] or {}; table.insert(handlers[name], fn) end
    function RegisterNetEvent(name, fn) if fn then AddEventHandler(name, fn) end end
    function TriggerEvent(name, ...) for _, fn in ipairs(handlers[name] or {}) do fn(...) end end
    function TriggerClientEvent() end
    function Wait(delay) if onWait then onWait(delay) end end
    function IsPlayerAceAllowed() return false end
    function CreateThread() end
    function SetTimeout() end
    function CancelEvent() end
    table.create = function() return {} end
    local function query(sql, args)
        if onQuery then onQuery(sql, args) end
        if sql:find('SELECT inventory', 1, true) then return clone(inventoryRows[args[1]] or {}) end
        if sql:find('SELECT license', 1, true) then return rows[args[1]] and rows[args[1]].license end
        if sql:find('SELECT * FROM players where license', 1, true) then
            for _, data in pairs(rows) do if data.license == args[1] then return clone(data) end end
            return nil
        end
        if sql:find('SELECT * FROM players where', 1, true) then return clone(rows[args[1]]) end
        return 0
    end
    local prepare = setmetatable({ await = query }, { __call = function(_, sql, args)
        writes[#writes + 1] = { sql, clone(args) }
        if sql:find('UPDATE players SET inventory', 1, true) then inventoryRows[args[2]] = clone(args[1]) end
    end })
    MySQL = { prepare = prepare, scalar = { await = query }, single = { await = function() return nil end },
        insert = function(sql, data) rows[data.citizenid] = clone(data); writes[#writes + 1] = { sql, clone(data) } end,
        transaction = function(queries, callback) writes[#writes + 1] = queries; callback(true) end,
        Async = { fetchAll = function(sql, args, callback)
            if sql:find('playerskins', 1, true) then callback({ { skin = {}, clothes = {} } }); return end
            local result = {}; for _, row in pairs(rows) do if row.license == args['@license'] then result[#result + 1] = clone(row) end end
            callback(result)
        end }
    }
    exports = setmetatable({ ['rsg-core'] = { GetCoreObject = function() return RSGCore end },
        ['rsg-inventory'] = {
            LoadInventory = function(_, ...) return Inventory.LoadInventory(...) end,
            SaveInventory = function(_, ...) return Inventory.SaveInventory(...) end,
        } }, { __call = function() end })
    package.preload['shared.config'] = function() return {} end
    Inventory = { CheckItemDecay = function() return false, 100, false end }
    loadCfx('rsg-core/server/functions.lua')
    dofile(root .. 'rsg-core/server/local_sessions/identity.lua')
    dofile(root .. 'rsg-core/server/moneyitems.lua')
    dofile(root .. 'rsg-core/server/player.lua')
    dofile(root .. 'rsg-core/server/events.lua')
    loadCfx('rsg-inventory/server/exports.lua')
    dofile(root .. 'rsg-multicharacter/server/main.lua')
end
local function join(src, steam, lic, endpoint)
    players[src] = { steam = steam or owner, license = lic or license, endpoint = endpoint or '127.0.0.1',
        state = { set = function() end } }
    source = src; TriggerEvent('playerJoining')
end
local function drop(src)
    source = src; TriggerEvent('playerDropped', 'quit'); players[src] = nil
end
local function row(id, profile)
    rows[id] = { citizenid = id, license = profile, cid = 1, money = {}, job = {}, gang = {},
        position = {}, metadata = {}, charinfo = {} }
end
local function callback(name, src, ...)
    local value; RSGCore.ServerCallbacks[name](src, function(v) value = v end, ...); return value
end
local function connection(src)
    source = src; local reason
    TriggerEvent('playerConnecting', 'test', nil, { defer = function() end, update = function() end,
        done = function(v) reason = v end }); return reason
end

start(); join(1); join(2)
eq(RSGCore.LocalPeerIdentity.Resolve(1), license, 'primary identity')
eq(RSGCore.LocalPeerIdentity.Resolve(2), alias, 'peer identity')
eq(RSGCore.Functions.GetIdentifier(2, 'license'), license, 'canonical license unchanged')
eq(RSGCore.Functions.GetIdentifier(2, 'steam'), owner, 'canonical steam unchanged')
row('MAIN', license); row('PEER', alias)
eq(RSGCore.Player.Login(1, 'MAIN'), true, 'main login')
eq(RSGCore.Player.Login(2, 'PEER'), true, 'peer login')
eq(RSGCore.Functions.GetPlayerByLicense(alias), RSGCore.Players[2], 'alias lookup')
eq(RSGCore.Functions.GetPlayerByLicense(license), RSGCore.Players[1], 'native profile lookup')
RSGCore.Functions.SetPlayerBucket(1, 11); RSGCore.Functions.SetPlayerBucket(2, 22)
eq(RSGCore.Player_Buckets[license].id, 1, 'primary bucket')
eq(RSGCore.Player_Buckets[alias].id, 2, 'peer bucket')
RSGCore.Players[1].PlayerData.items = { { name = 'apple', amount = 3, slot = 1 } }
RSGCore.Players[2].PlayerData.items = { { name = 'apple', amount = 7, slot = 1 } }
RSGCore.Players[1].Functions.Save(); RSGCore.Players[2].Functions.Save()
eq(inventoryRows.MAIN[1].amount, 3, 'main inventory persistence')
eq(inventoryRows.PEER[1].amount, 7, 'peer inventory persistence')
for src, amount in pairs({ [1] = 3, [2] = 7 }) do
    local player = RSGCore.Players[src]
    player.PlayerData.items = { { name = 'dollar', amount = amount, slot = 1 } }
    player.PlayerData.money.cash = amount
    source = src; TriggerEvent('RSGCore:Server:OnPlayerLoaded')
    player.PlayerData.items[1].amount = amount + 1
    player.Functions.UpdatePlayerData()
end
eq(RSGCore.Players[1].PlayerData.money.cash, 4, 'primary money item synchronization')
eq(RSGCore.Players[2].PlayerData.money.cash, 8, 'peer money item synchronization')
drop(1)
eq(RSGCore.Players[2].PlayerData.citizenid, 'PEER', 'peer survives main disconnect')
eq(RSGCore.Player_Buckets[alias].id, 2, 'peer bucket survives main disconnect')
eq(RSGCore.Player_Buckets[license], nil, 'main bucket removed')
eq(RSGCore.Functions.GetSource(license), 0, 'real profile does not resolve to alias peer')
eq(RSGCore.Functions.GetSource(owner), 0, 'native steam lookup does not resolve to alias peer')
eq(RSGCore.Functions.GetPlayer(license), nil, 'real profile player absent after disconnect')
local offlineMain = RSGCore.Player.GetPlayerByLicense(license)
eq(offlineMain.Offline, true, 'real profile lookup remains offline')
eq(offlineMain.PlayerData.license, license, 'offline lookup does not cross profiles')
eq(RSGCore.Functions.GetPlayerByLicense(alias), RSGCore.Players[2], 'alias lookup survives primary disconnect')
eq(callback('rsg-multicharacter:server:setupCharacters', 2)[1].citizenid, 'PEER', 'separate character list')
eq(callback('rsg-multicharacter:server:getAppearance', 2, 'MAIN'), nil, 'other profile appearance denied')
local before = #writes
source = 2; TriggerEvent('rsg-multicharacter:server:deleteCharacter', 'MAIN')
eq(#writes, before, 'other profile deletion writes nothing')
eq(dropped[2] ~= nil, true, 'other profile deletion rejected')

start(); join(1); join(2)
eq(RSGCore.Player.Login(2, false, { cid = 1, charinfo = {} }), true, 'new peer character')
eq(rows.NEW1.license, alias, 'new character alias ownership')
eq(RSGCore.Players[2].PlayerData.citizenid, 'NEW1', 'new character loaded normally')
eq(RSGCore.Player.Login(2, false, { cid = 2, charinfo = {} }), false, 'double login rejected')
join(3); eq(dropped[3] ~= nil, true, 'third owner rejected')
eq(RSGCore.LocalPeerIdentity.Resolve(3), nil, 'third owner has no profile')

start(); join(1); join(2)
row('PEER', alias)
local pending = coroutine.create(function() return RSGCore.Player.Login(2, 'PEER') end)
onQuery = function(sql) if sql:find('SELECT * FROM players where', 1, true) then coroutine.yield() end end
eq(coroutine.resume(pending), true)
eq(RSGCore.Player.Login(2, 'PEER'), false, 'pending duplicate login denied')
drop(2); join(2)
onQuery = nil
local ok, result = coroutine.resume(pending)
eq(ok, true); eq(result, false, 'stale DB continuation rejected')
eq(RSGCore.Players[2], nil, 'stale login did not attach to reused source')
eq(RSGCore.Player.Login(2, 'PEER'), true, 'new session may login')

start(); join(1); join(2); row('PEER', alias)
eq(RSGCore.Player.Login(2, 'PEER'), true)
pending = coroutine.create(function() RSGCore.Player.Logout(2) end)
onWait = function(delay) if delay == 200 then coroutine.yield() end end
eq(coroutine.resume(pending), true)
eq(RSGCore.Player.Login(2, false, { cid = 2 }), false, 'login while unloading rejected')
drop(2); join(2); onWait = nil
eq(RSGCore.Player.Login(2, 'PEER'), true)
local replacement = RSGCore.Players[2]
eq(coroutine.resume(pending), true)
eq(RSGCore.Players[2], replacement, 'stale logout preserves replacement player')

start({}); join(1); join(2); row('MAIN', license)
eq(RSGCore.Player.Login(1, 'MAIN'), true)
eq(RSGCore.Player.Login(2, 'MAIN'), false, 'active citizenid cannot load twice')

start(); join(1); join(2); row('PEER', alias)
pending = coroutine.create(function() return RSGCore.Player.Login(2, 'PEER') end)
onWait = function(delay) if delay == 1000 then coroutine.yield() end end
eq(coroutine.resume(pending), true)
drop(2); join(2); onWait = nil
ok, result = coroutine.resume(pending)
eq(ok, true); eq(result, false, 'stale player-loaded continuation rejected')
eq(RSGCore.Players[2], nil, 'stale player-loaded callback did not attach new session')

start(); join(1); join(2); row('PEER', alias)
pending = coroutine.create(function() return RSGCore.Player.Login(2, 'PEER') end)
onQuery = function(sql) if sql:find('SELECT inventory', 1, true) then coroutine.yield() end end
eq(coroutine.resume(pending), true); drop(2); join(2); onQuery = nil
ok, result = coroutine.resume(pending)
eq(ok, true); eq(result, false, 'stale inventory continuation rejected')
eq(RSGCore.Players[2], nil, 'stale inventory load did not create player')

start(); join(1); join(2); row('MAIN', license)
eq(RSGCore.Player.Login(2, 'MAIN'), false, 'cross-profile login denied')
eq(RSGCore.Players[2], nil, 'cross-profile player absent')
join(3, owner, license, '192.168.1.2'); eq(dropped[3] ~= nil, true, 'remote owner rejected')
join(4, owner, 'license:deadbeef'); eq(dropped[4] ~= nil, true, 'owner license mismatch rejected')

start(); join(1)
players[2] = { steam = owner, license = license, endpoint = '127.0.0.1' }
eq(connection(2), nil, 'eligible peer passes duplicate deferral')
players[2].endpoint = '192.168.1.2'
eq(connection(2), 'error.duplicate_license', 'remote duplicate rejected')
players[2].endpoint = '127.0.0.1'
local banCheck = RSGCore.Functions.IsPlayerBanned
RSGCore.Functions.IsPlayerBanned = function(src)
    eq(RSGCore.Functions.GetIdentifier(src, 'license'), license, 'ban uses real identity'); return true, 'banned'
end
eq(connection(2), 'banned', 'peer still subject to bans')
RSGCore.Functions.IsPlayerBanned = banCheck

start({}); join(1); join(2)
eq(RSGCore.LocalPeerIdentity.Resolve(2), license, 'disabled mode does not alias')
eq(connection(2), 'error.duplicate_license', 'disabled mode keeps duplicate check')
start({ redm_local_mode = 'two_player' })
join(9, 'steam:deadbeef', 'license:deadbeef', '198.51.100.9')
join(1); join(2, owner, license, '[::1]')
eq(RSGCore.LocalPeerIdentity.Resolve(1), license, 'automatic primary')
eq(RSGCore.LocalPeerIdentity.Resolve(2), alias, 'automatic peer')
eq(RSGCore.LocalPeerIdentity.Resolve(9), 'license:deadbeef', 'remote does not bind automatic owner')
row('MAIN', license); row('PEER', alias)
eq(RSGCore.Player.Login(1, 'MAIN'), true, 'automatic primary normal login')
eq(RSGCore.Player.Login(2, 'PEER'), true, 'automatic peer normal login')
eq(RSGCore.Functions.GetIdentifier(2, 'license'), license, 'automatic native identity unchanged')
drop(1); drop(2)
join(3, 'steam:deadbeef', 'license:deadbeef'); join(4); join(5)
eq(RSGCore.LocalPeerIdentity.Resolve(3), 'license:deadbeef', 'automatic binding fixed until restart')
eq(RSGCore.LocalPeerIdentity.Resolve(4), license, 'fixed owner reconnects')
eq(RSGCore.LocalPeerIdentity.Resolve(5), alias, 'fixed peer reconnects')

for _, settings in ipairs({
    { redm_local_mode = 'two_player', redm_local_owner_steam = owner },
    { redm_local_mode = 'two_player', redm_local_owner_license = license },
    { redm_local_mode = 'two_player', redm_local_owner_steam = 'invalid', redm_local_owner_license = license },
    { redm_local_mode = 'two_player', redm_local_alias = 'license:abcdef' },
}) do
    start(settings); join(1)
    eq(dropped[1] ~= nil, true, 'invalid or partial configured owner rejected')
    eq(RSGCore.LocalPeerIdentity.Resolve(1), nil, 'invalid settings expose no profile')
end
start(); join(1, owner, license, '198.51.100.1')
eq(dropped[1] ~= nil, true, 'explicit primary requires loopback')
print('RSG actual-source identity and inventory tests passed')

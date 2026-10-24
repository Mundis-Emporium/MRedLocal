LocalPeerIdentity = {}

local owner = GetConvar('redm_local_owner_steam', '')
local license = GetConvar('redm_local_owner_license', '')
local alias = GetConvar('redm_local_alias', 'localtest:secondary')
local enabled = GetConvar('redm_local_mode', '') == 'two_player'
local automatic = owner == '' and license == ''
local validOwner = automatic or (owner:match('^steam:[0-9a-fA-F]+$') ~= nil
    and license:match('^license:[0-9a-fA-F]+$') ~= nil
    and #owner <= 50 and #license <= 50)
local aliasEnabled = enabled and validOwner and alias:match('^localtest:[%w_]+$') ~= nil and #alias <= 50
local sessions = {}
local primary
local peer
local generation = 0

local function key(source)
    return tostring(source)
end

local function isLoopback(source)
    local endpoint = GetPlayerEndpoint(source)
    return endpoint == '127.0.0.1' or endpoint == '::1' or endpoint == '[::1]'
end

local function reject(source)
    generation = generation + 1
    local session = { source = source, identifier = false, generation = generation }
    sessions[key(source)] = session
    return nil, session
end

function LocalPeerIdentity.Reserve(source, steam, realLicense)
    local sourceKey = key(source)
    local previous = sessions[sourceKey]
    if previous and not previous.dropping then
        return nil, previous
    end

    if enabled and isLoopback(source) then
        if not aliasEnabled then return reject(source) end
        if automatic and owner == '' then
            if type(steam) ~= 'string' or not steam:match('^steam:[0-9a-fA-F]+$') or #steam > 50
                or type(realLicense) ~= 'string' or not realLicense:match('^license:[0-9a-fA-F]+$') or #realLicense > 50
                or steam ~= GetPlayerIdentifierByType(source, 'steam')
                or realLicense ~= GetPlayerIdentifierByType(source, 'license') then return reject(source) end
            owner, license = steam, realLicense
        end
    end

    generation = generation + 1
    local session = {
        source = source,
        identifier = steam,
        license = realLicense,
        loadingKey = realLicense,
        generation = generation,
    }

    if enabled and steam == owner then
        if realLicense ~= license or not isLoopback(source) then
            return reject(source)
        end
        if not primary then
            primary = session
        elseif aliasEnabled and not peer and isLoopback(source) then
            session.identifier = alias
            session.loadingKey = alias
            session.isAlias = true
            peer = session
        else
            return reject(source)
        end
    end

    sessions[sourceKey] = session
    return session.identifier, session
end

function LocalPeerIdentity.Resolve(source)
    local session = sessions[key(source)]
    if session then
        return session.identifier or nil
    end

    local steam = GetPlayerIdentifierByType(source, 'steam')
    if enabled and steam == owner and (primary or peer) then
        return nil
    end
    return steam
end

function LocalPeerIdentity.Session(source)
    return sessions[key(source)]
end

function LocalPeerIdentity.LoadedUser(source)
    local session = sessions[key(source)]
    if not session or not session.user or not session.identifier then return nil end
    if _users[session.identifier] ~= session.user then return nil end
    if key(session.user.source) ~= key(source) then return nil end
    return session.user, session
end

function LocalPeerIdentity.IsAdditionalOwnerConnection(source)
    if not enabled or not primary or primary.source == source then return false end
    return isLoopback(source) and GetPlayerIdentifierByType(source, 'steam') == owner
        and GetPlayerIdentifierByType(source, 'license') == license
end

function LocalPeerIdentity.HasOtherOwnerConnection(source)
    if not enabled then return false end
    local session = sessions[key(source)]
    if session ~= primary and session ~= peer then return false end
    return (primary and primary ~= session and not primary.dropping)
        or (peer and peer ~= session and not peer.dropping)
        or false
end

function LocalPeerIdentity.CanonicalOwnerIdentifier(identifier)
    if aliasEnabled and owner ~= '' and identifier == alias then return owner end
    return identifier
end

function LocalPeerIdentity.Release(source, session)
    if not session or sessions[key(source)] ~= session then return end
    session.dropping = true
    if primary == session then primary = nil end
    if peer == session then peer = nil end
    SetTimeout(0, function()
        if sessions[key(source)] == session then
            session.identifier = false
            session.user = nil
        end
    end)
end

-- SPDX-License-Identifier: GPL-3.0-only
RSGCore.LocalPeerIdentity = {}
local Identity = RSGCore.LocalPeerIdentity
local owner = GetConvar('redm_local_owner_steam', '')
local license = GetConvar('redm_local_owner_license', '')
local alias = GetConvar('redm_local_alias', 'localtest:secondary')
local enabled = GetConvar('redm_local_mode', '') == 'two_player'
local automatic = owner == '' and license == ''
local validOwner = automatic or (owner:match('^steam:[0-9a-fA-F]+$') ~= nil
    and license:match('^license:[0-9a-fA-F]+$') ~= nil
    and #owner <= 50 and #license <= 50)
local aliasEnabled = enabled and validOwner and alias:match('^localtest:[%w_]+$') ~= nil and #alias <= 50
local sessions, claims = {}, {}
local primary, peer

local function loopback(src)
    local endpoint = GetPlayerEndpoint(src)
    return endpoint == '127.0.0.1' or endpoint == '::1' or endpoint == '[::1]'
end

function Identity.CanConnect(src)
    return aliasEnabled and loopback(src) and (not primary or not peer)
        and GetPlayerIdentifierByType(src, 'steam') == owner
        and GetPlayerIdentifierByType(src, 'license') == license
end

function Identity.Session(src)
    return sessions[tostring(src)]
end

function Identity.Current(src, session)
    return session ~= nil and sessions[tostring(src)] == session and not session.dropped
        and session.license == GetPlayerIdentifierByType(src, 'license')
        and session.steam == GetPlayerIdentifierByType(src, 'steam')
end

function Identity.Resolve(src)
    local session = Identity.Session(src)
    if Identity.Current(src, session) then return session.profile end
end

function Identity.Release(src, session)
    if not Identity.Current(src, session) and sessions[tostring(src)] ~= session then return end
    if not session then return end
    session.dropped = true
    if session.claim and claims[session.claim] == session then claims[session.claim] = nil end
    sessions[tostring(src)] = nil
    if primary == session then primary = nil end
    if peer == session then peer = nil end
end

function Identity.BeginLogin(src, citizenid)
    local session = Identity.Session(src)
    if not Identity.Current(src, session) or session.loading or session.unloading or RSGCore.Players[src] then return end
    if citizenid and (type(citizenid) ~= 'string' or citizenid == '' or #citizenid > 50
        or claims[citizenid] or RSGCore.Functions.GetPlayerByCitizenId(citizenid)) then return end
    session.loading = true
    session.claim = citizenid or nil
    if citizenid then claims[citizenid] = session end
    return session
end

function Identity.EndLogin(session)
    if session.claim and claims[session.claim] == session then claims[session.claim] = nil end
    session.claim = nil
    session.loading = false
end

function Identity.CanCreate(src, data)
    local session = Identity.Session(src)
    return Identity.Current(src, session) and session.loading and not session.unloading
        and data.license == session.profile and not RSGCore.Players[src]
        and not RSGCore.Functions.GetPlayerByCitizenId(data.citizenid)
end

AddEventHandler('playerJoining', function()
    local src = source
    local old = Identity.Session(src)
    if old then
        DropPlayer(src, 'Local testing session is already bound. Reconnect.')
        return
    end
    local steam = GetPlayerIdentifierByType(src, 'steam')
    local realLicense = GetPlayerIdentifierByType(src, 'license')
    if not realLicense then DropPlayer(src, 'No valid license.'); return end
    if enabled and loopback(src) then
        if not aliasEnabled then DropPlayer(src, 'Invalid local testing owner settings.'); return end
        if automatic and owner == '' then
            if type(steam) ~= 'string' or not steam:match('^steam:[0-9a-fA-F]+$') or #steam > 50
                or not realLicense:match('^license:[0-9a-fA-F]+$') or #realLicense > 50 then
                DropPlayer(src, 'Local testing requires valid Steam and license identifiers.'); return
            end
            owner, license = steam, realLicense
        end
    end
    local session = { source = src, steam = steam, license = realLicense, profile = realLicense }
    if enabled and steam == owner then
        if realLicense ~= license or not loopback(src) then
            DropPlayer(src, 'Local testing requires the configured owner on loopback.'); return
        elseif not primary then
            primary = session
        elseif aliasEnabled and not peer then
            session.profile = alias
            peer = session
        else
            DropPlayer(src, 'Local testing allows two owner sessions.'); return
        end
    end
    sessions[tostring(src)] = session
end)

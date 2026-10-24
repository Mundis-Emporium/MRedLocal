"""Rebuild the pinned adapter from the unmodified RSG baseline (maintainer tool)."""
import difflib
import hashlib
import json
import os
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
HERE = REPO / 'adapters/rsg'
BASE = Path(os.environ['RSG_BASELINE_DIR'])
FILES = {}


def load(rel):
    raw = (BASE / rel).read_bytes()
    text = raw.decode('utf-8').replace('\r\n', '\n')
    FILES[rel] = [raw, text, text]
    return text


def change(rel, old, new, count=1):
    if rel not in FILES:
        load(rel)
    assert FILES[rel][2].count(old) == count, (rel, old[:100])
    FILES[rel][2] = FILES[rel][2].replace(old, new)


change('rsg-core/fxmanifest.lua', "    'server/functions.lua',", "    'server/functions.lua',\n    'server/local_sessions/identity.lua',")
change('rsg-core/server/functions.lua', '    for src, _ in pairs(RSGCore.Players) do\n        local idens', '''    for src, player in pairs(RSGCore.Players) do
        if player.PlayerData.license == identifier then return src end
    end
    for src, _ in pairs(RSGCore.Players) do
        local idens''')
change('rsg-core/server/functions.lua', '''            if identifier == id then
                return src''', '''            if identifier == id and RSGCore.Players[src].PlayerData.license == RSGCore.Functions.GetIdentifier(src, 'license') then
                return src''')
change('rsg-core/server/functions.lua', "local plicense = RSGCore.Functions.GetIdentifier(source, 'license')\n        Player(source)", "local plicense = RSGCore.LocalPeerIdentity.Resolve(source)\n        if not plicense then return false end\n        Player(source)")
change('rsg-core/server/events.lua', '''    if not RSGCore.Players[src] then return end
    local Player = RSGCore.Players[src]''', '''    local session = RSGCore.LocalPeerIdentity.Session(src)
    local Player = RSGCore.Players[src]
    RSGCore.LocalPeerIdentity.Release(src, session)
    if not Player then return end''')
change('rsg-core/server/events.lua', '''    Player.Functions.Save()
    RSGCore.Player_Buckets[Player.PlayerData.license] = nil
    RSGCore.Players[src] = nil''', '''    if RSGCore.Players[src] ~= Player then return end
    Player.Functions.Save()
    local bucket = RSGCore.Player_Buckets[Player.PlayerData.license]
    if bucket and bucket.id == src then RSGCore.Player_Buckets[Player.PlayerData.license] = nil end
    if RSGCore.Players[src] == Player then RSGCore.Players[src] = nil end''')
change('rsg-core/server/events.lua', 'elseif RSGCore.Config.Server.CheckDuplicateLicense and RSGCore.Functions.IsLicenseInUse(license) then', 'elseif RSGCore.Config.Server.CheckDuplicateLicense and RSGCore.Functions.IsLicenseInUse(license)\n        and not RSGCore.LocalPeerIdentity.CanConnect(src) then')
rel = 'rsg-core/server/player.lua'
text = load(rel)
start, end = text.index('function RSGCore.Player.Login('), text.index('\nfunction RSGCore.Player.GetOfflinePlayer(')
FILES[rel][2] = text[:start] + '''function RSGCore.Player.Login(source, citizenid, newData)
    local identity = RSGCore.LocalPeerIdentity
    local session = identity.BeginLogin(source, citizenid)
    if not session then return false end
    local ok, result = pcall(function()
        local PlayerData
        if citizenid then
            PlayerData = MySQL.prepare.await('SELECT * FROM players where citizenid = ?', { citizenid })
            if not identity.Current(source, session) then return false end
            if not PlayerData or PlayerData.license ~= session.profile then
                DropPlayer(source, Lang:t('info.exploit_dropped'))
                return false
            end
            PlayerData.money = json.decode(PlayerData.money)
            PlayerData.job = json.decode(PlayerData.job)
            PlayerData.gang = json.decode(PlayerData.gang)
            PlayerData.position = json.decode(PlayerData.position)
            PlayerData.metadata = json.decode(PlayerData.metadata)
            PlayerData.charinfo = json.decode(PlayerData.charinfo)
        else
            if type(newData) ~= 'table' or newData.citizenid then return false end
            PlayerData = newData
            PlayerData.license = session.profile
        end
        return RSGCore.Player.CheckPlayerData(source, PlayerData) ~= nil
    end)
    identity.EndLogin(session)
    if not ok then error(result) end
    return result
end
''' + text[end:]
change(rel, "PlayerData.license = PlayerData.license or RSGCore.Functions.GetIdentifier(source, 'license')", "PlayerData.license = PlayerData.license or RSGCore.LocalPeerIdentity.Resolve(source)")
change(rel, '''    if GetResourceState('rsg-inventory') ~= 'missing' then
        PlayerData.items = exports['rsg-inventory']:LoadInventory(PlayerData.source, PlayerData.citizenid)
    end

    return RSGCore.Player.CreatePlayer(PlayerData, Offline)''', '''    local session = source and RSGCore.LocalPeerIdentity.Session(source)
    if GetResourceState('rsg-inventory') ~= 'missing' then
        PlayerData.items = exports['rsg-inventory']:LoadInventory(PlayerData.source, PlayerData.citizenid)
    end
    if source and not RSGCore.LocalPeerIdentity.Current(source, session) then return end
    return RSGCore.Player.CreatePlayer(PlayerData, Offline)''')
change(rel, '''function RSGCore.Player.Logout(source)
    TriggerClientEvent''', '''function RSGCore.Player.Logout(source)
    local player = RSGCore.Players[source]
    local session = RSGCore.LocalPeerIdentity.Session(source)
    if not player or not RSGCore.LocalPeerIdentity.Current(source, session) then return end
    session.unloading = true
    TriggerClientEvent''')
change(rel, '''    Wait(200)
    RSGCore.Players[source] = nil''', '''    Wait(200)
    if RSGCore.Players[source] == player then RSGCore.Players[source] = nil end
    if RSGCore.LocalPeerIdentity.Current(source, session) then session.unloading = false end''')
change(rel, '''function RSGCore.Player.CreatePlayer(PlayerData, Offline)
    local self''', '''function RSGCore.Player.CreatePlayer(PlayerData, Offline)
    local session = not Offline and RSGCore.LocalPeerIdentity.Session(PlayerData.source)
    if not Offline and not RSGCore.LocalPeerIdentity.CanCreate(PlayerData.source, PlayerData) then return end
    local self''')
change(rel, '''        TriggerEvent('RSGCore:Server:PlayerLoaded', self)
        self.Functions.UpdatePlayerData()''', '''        TriggerEvent('RSGCore:Server:PlayerLoaded', self)
        if not RSGCore.LocalPeerIdentity.Current(PlayerData.source, session)
            or RSGCore.Players[PlayerData.source] ~= self then return end
        self.Functions.UpdatePlayerData()''')
change(rel, '''        self.Functions.UpdatePlayerData()
    end
end

-- Add a new function''', '''        self.Functions.UpdatePlayerData()
        return self
    end
end

-- Add a new function''')
change(rel, '''function RSGCore.Player.DeleteCharacter(source, citizenid)
    local license = RSGCore.Functions.GetIdentifier(source, 'license')
    local result = MySQL.scalar.await('SELECT license FROM players where citizenid = ?', { citizenid })
    if license == result then''', '''function RSGCore.Player.DeleteCharacter(source, citizenid)
    local identity = RSGCore.LocalPeerIdentity
    local session = identity.Session(source)
    if not identity.Current(source, session) or type(citizenid) ~= 'string' then return false end
    local license = session.profile
    local result = MySQL.scalar.await('SELECT license FROM players where citizenid = ?', { citizenid })
    if not identity.Current(source, session) then return false end
    if RSGCore.Functions.GetPlayerByCitizenId(citizenid) or session.loading then return false end
    if license == result then''')
rel = 'rsg-multicharacter/server/main.lua'
change(rel, '''AddEventHandler('RSGCore:Server:PlayerLoaded', function(Player)
    Wait(1000) -- 1 second should be enough to do the preloading in other resources
    hasDonePreloading[Player.PlayerData.source] = true
end)''', '''AddEventHandler('RSGCore:Server:PlayerLoaded', function(Player)
    local src = Player.PlayerData.source
    local identity = RSGCore.LocalPeerIdentity
    local session = identity.Session(src)
    Wait(1000)
    if identity.Current(src, session) and RSGCore.Players[src] == Player then
        hasDonePreloading[src] = true
    end
end)''')
change(rel, '''    MySQL.update('DELETE FROM playerskins WHERE citizenid = ?', {citizenid})
    RSGCore.Player.DeleteCharacter''', '''    RSGCore.Player.DeleteCharacter''')
change(rel, '''    local license = RSGCore.Functions.GetIdentifier(src, 'license')
    local plyChars = {}''', '''    local identity = RSGCore.LocalPeerIdentity
    local session = identity.Session(src)
    local license = identity.Resolve(src)
    if not license then cb({}); return end
    local plyChars = {}''')
change(rel, '''        for i = 1, (#result), 1 do''', '''        if not identity.Current(src, session) then cb({}); return end
        for i = 1, (#result), 1 do''')
change(rel, '''    MySQL.Async.fetchAll('SELECT * FROM playerskins WHERE citizenid = ?', { citizenid}, function(result)''', '''    local identity = RSGCore.LocalPeerIdentity
    local session = identity.Session(source)
    if not identity.Current(source, session) or type(citizenid) ~= 'string' then cb(nil); return end
    local owner = MySQL.scalar.await('SELECT license FROM players WHERE citizenid = ?', { citizenid })
    if not identity.Current(source, session) or owner ~= session.profile then cb(nil); return end
    MySQL.Async.fetchAll('SELECT * FROM playerskins WHERE citizenid = ?', { citizenid}, function(result)
        if not identity.Current(source, session) then cb(nil); return end''')
change(rel, '''    if RSGCore.Player.Login(src, cData.citizenid) then''', '''    if type(cData) ~= 'table' or type(cData.citizenid) ~= 'string' then return end
    local identity = RSGCore.LocalPeerIdentity
    local session = identity.Session(src)
    if RSGCore.Player.Login(src, cData.citizenid) then''')
change(rel, '''    newData.cid = data.cid''', '''    if type(data) ~= 'table' then return end
    local identity = RSGCore.LocalPeerIdentity
    local session = identity.Session(src)
    newData.cid = data.cid''')
change(rel, '''        until hasDonePreloading[src]
        RSGCore''', '''        until hasDonePreloading[src] or not identity.Current(src, session)
        if not identity.Current(src, session) then return end
        RSGCore''', count=2)

helper = (HERE / 'identity.lua').read_text(encoding='utf-8')
FILES['rsg-core/server/local_sessions/identity.lua'] = [None, '', helper]
patch, entries = [], []
for rel, (raw, old, new) in sorted(FILES.items()):
    result = new.replace('\n', '\r\n').encode() if raw and b'\r\n' in raw else new.encode()
    patch.extend(difflib.unified_diff(old.splitlines(True), new.splitlines(True),
                                    fromfile='a/' + rel if raw is not None else '/dev/null', tofile='b/' + rel))
    entries.append({'path': rel, 'before_sha256': hashlib.sha256(raw).hexdigest() if raw else None,
                    'after_sha256': hashlib.sha256(result).hexdigest()})
manifest = {'format': 1, 'adapter': 'RSG core 2.3.10 + multicharacter 2.3.5 local two player',
            'version_file': 'rsg-core/fxmanifest.lua', 'required_version': "version '2.3.10'", 'files': entries}
manifest['read_only'] = [{'path': rel, 'sha256': hashlib.sha256((BASE / rel).read_bytes()).hexdigest()}
                         for rel in ['rsg-multicharacter/fxmanifest.lua', 'rsg-inventory/fxmanifest.lua',
                                     'rsg-inventory/server/exports.lua', 'rsg-core/server/moneyitems.lua']]
(HERE / 'rsg.patch').write_text(''.join(patch), encoding='utf-8', newline='\n')
(HERE / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8', newline='\n')

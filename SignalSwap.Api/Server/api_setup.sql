USE Networks;
GO

IF COL_LENGTH('dbo.signal_reading', 'ssid') IS NULL
    ALTER TABLE dbo.signal_reading ADD ssid NVARCHAR(32) NULL;
GO

UPDATE r SET ssid = w.ssid
FROM dbo.signal_reading r
JOIN dbo.wifi_network w ON w.id = r.wifi_network_id
WHERE r.ssid IS NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.signal_reading') AND name = 'ssid' AND is_nullable = 1)
    ALTER TABLE dbo.signal_reading ALTER COLUMN ssid NVARCHAR(32) NOT NULL;
GO

IF COL_LENGTH('dbo.dead_zone', 'ssid') IS NULL
    ALTER TABLE dbo.dead_zone ADD ssid NVARCHAR(32) NULL;
GO

UPDATE dz SET ssid = w.ssid
FROM dbo.dead_zone dz
JOIN dbo.wifi_network w ON w.id = dz.wifi_network_id
WHERE dz.ssid IS NULL;
GO

IF COL_LENGTH('dbo.app_user', 'failed_logins') IS NULL
    ALTER TABLE dbo.app_user ADD failed_logins INT NOT NULL CONSTRAINT DF_app_user_failed_logins DEFAULT 0;
GO

IF COL_LENGTH('dbo.app_user', 'locked_until') IS NULL
    ALTER TABLE dbo.app_user ADD locked_until DATETIME2 NULL;
GO

IF COL_LENGTH('dbo.app_user', 'token_version') IS NULL
    ALTER TABLE dbo.app_user ADD token_version INT NOT NULL CONSTRAINT DF_app_user_token_version DEFAULT 0;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID('dbo.signal_reading') AND i.is_unique = 1 AND c.name = 'client_id')
    CREATE UNIQUE INDEX UX_signal_reading_client_id ON dbo.signal_reading (client_id);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID('dbo.wifi_network') AND i.is_unique = 1 AND c.name = 'bssid')
    CREATE UNIQUE INDEX UX_wifi_network_bssid ON dbo.wifi_network (bssid);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID('dbo.app_user') AND i.is_unique = 1 AND c.name = 'username')
    CREATE UNIQUE INDEX UX_app_user_username ON dbo.app_user (username);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID('dbo.app_user') AND i.is_unique = 1 AND c.name = 'email')
    CREATE UNIQUE INDEX UX_app_user_email ON dbo.app_user (email);
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_signal_reading_user_network' AND object_id = OBJECT_ID('dbo.signal_reading'))
    DROP INDEX IX_signal_reading_user_network ON dbo.signal_reading;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_dead_zone_user_network' AND object_id = OBJECT_ID('dbo.dead_zone'))
    DROP INDEX IX_dead_zone_user_network ON dbo.dead_zone;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_signal_reading_user_ssid' AND object_id = OBJECT_ID('dbo.signal_reading'))
    CREATE INDEX IX_signal_reading_user_ssid
        ON dbo.signal_reading (recorded_by, ssid, floor)
        INCLUDE (latitude, longitude, signal_strength, recorded_at, wifi_network_id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_dead_zone_user_ssid' AND object_id = OBJECT_ID('dbo.dead_zone'))
    CREATE INDEX IX_dead_zone_user_ssid
        ON dbo.dead_zone (reported_by, ssid, floor)
        INCLUDE (latitude, longitude, resolved_at, source);
GO

SELECT t.name AS table_name, c.name AS column_name, ty.name AS data_type, c.is_nullable
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE (t.name = 'signal_reading' AND c.name = 'ssid')
   OR (t.name = 'dead_zone' AND c.name = 'ssid')
   OR (t.name = 'app_user' AND c.name IN ('failed_logins', 'locked_until', 'token_version'))
ORDER BY t.name, c.name;

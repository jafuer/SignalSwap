USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT SERVICE\SignalSwapApi')
    CREATE LOGIN [NT SERVICE\SignalSwapApi] FROM WINDOWS;
GO

USE Networks;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'NT SERVICE\SignalSwapApi')
    CREATE USER [NT SERVICE\SignalSwapApi] FOR LOGIN [NT SERVICE\SignalSwapApi];
GO

ALTER ROLE db_datareader ADD MEMBER [NT SERVICE\SignalSwapApi];
ALTER ROLE db_datawriter ADD MEMBER [NT SERVICE\SignalSwapApi];
GO

SELECT dp.name AS database_user, r.name AS role_name
FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals dp ON dp.principal_id = rm.member_principal_id
WHERE dp.name = N'NT SERVICE\SignalSwapApi';

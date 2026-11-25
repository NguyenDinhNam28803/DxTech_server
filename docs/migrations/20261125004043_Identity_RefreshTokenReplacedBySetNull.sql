-- Sinh bởi: dotnet tool run dotnet-ef migrations script Identity_Initial 20261125004043_Identity_RefreshTokenReplacedBySetNull --project server/server.csproj
-- PR-8: FK replaced_by_id RESTRICT → SET NULL (xoá tài khoản có token đã xoay vòng, job dọn token theo lô).

START TRANSACTION;

ALTER TABLE identity.refresh_tokens DROP CONSTRAINT fk_refresh_tokens_refresh_tokens_replaced_by_id;

ALTER TABLE identity.refresh_tokens ADD CONSTRAINT fk_refresh_tokens_refresh_tokens_replaced_by_id FOREIGN KEY (replaced_by_id) REFERENCES identity.refresh_tokens (id) ON DELETE SET NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261125004043_Identity_RefreshTokenReplacedBySetNull', '8.0.8');

COMMIT;


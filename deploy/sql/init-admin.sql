-- First admin user for AwardsFerm
-- Login: admin
-- Password (example): Admin123!
-- Change password immediately after first login.

INSERT INTO users (Login, PasswordHash, PasswordSalt, CreatedAt)
VALUES (
  'admin',
  'IfeI0vHx3ax6YDwK5m1nLPLGZQqkFTgmVN9LQ7B2/fk=',
  '427gUPHEgmWKK9DZuqqcJw==',
  CURRENT_TIMESTAMP
);

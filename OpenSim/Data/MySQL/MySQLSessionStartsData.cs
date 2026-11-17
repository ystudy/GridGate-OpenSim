// GridGate: records where each session starts, for the money service. See MERGING.md.
using System;
using OpenMetaverse;
using MySql.Data.MySqlClient;

namespace OpenSim.Data.MySQL
{
    public class MySQLSessionStartsData : MySQLGenericTableHandler<SessionStartRow>, ISessionStartsData
    {
        public MySQLSessionStartsData(string connectionString, string realm)
            : base(connectionString, realm, "GridGateSessionStarts") { }

        public void Record(UUID sessionID, UUID userID, UUID regionID, string serverIP, int? serverPort, string source)
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = string.Format(
                    "insert into {0} (SessionID, UserID, RegionID, ServerIP, ServerPort, Source) " +
                    "values (?SessionID, ?UserID, ?RegionID, ?ServerIP, ?ServerPort, ?Source)", m_Realm);
                cmd.Parameters.AddWithValue("?SessionID", sessionID.ToString());
                cmd.Parameters.AddWithValue("?UserID", userID.ToString());
                cmd.Parameters.AddWithValue("?RegionID", regionID.ToString());
                // An address with no port, or the other way round, is no address.
                bool known = serverIP is not null && serverPort is not null;
                cmd.Parameters.AddWithValue("?ServerIP", known ? (object)serverIP : DBNull.Value);
                cmd.Parameters.AddWithValue("?ServerPort", known ? (object)serverPort.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("?Source", source);
                ExecuteNonQuery(cmd);
            }
        }

        public void DeleteOld()
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = string.Format("delete from {0} where At < NOW() - INTERVAL 1 DAY", m_Realm);
                ExecuteNonQuery(cmd);
            }
        }
    }
}

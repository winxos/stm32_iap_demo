using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.InteropServices;

namespace HELUO_IAP
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {

        }
        const string server_version_address = "https://raw.githubusercontent.com/winxos/files/master/config.json";
        class FirmWare
        {
            public byte[] data;
            public byte block_size;
            public byte sending_index;
        }
        private void button2_Click(object sender, EventArgs e)
        {
            openFileDialog1.Filter = "固件|*.bin";
            openFileDialog1.Title = "请选择固件进行升级";
            DialogResult d= openFileDialog1.ShowDialog();
            if(d==DialogResult.OK)
            {
                FileStream fs = new FileStream(openFileDialog1.FileName, FileMode.Open);
                BinaryReader binReader = new BinaryReader(fs);
                _fw.data = new byte[fs.Length];
                binReader.Read(_fw.data, 0, (int)fs.Length);
                binReader.Close();
                fs.Close();
                _fw.sending_index = 0;
                _fw.block_size = 200;
                toolStripStatusLabel1.Text = "加载固件" + openFileDialog1.FileName;
                textBox1.Text = String.Format("固件名称：{0}\r\n固件大小：{1} Bytes",Path.GetFileName(openFileDialog1.FileName), _fw.data.Length);
            }

        }
        enum CMD
        {
            INFO=0xf0,DATA,TIMEOUT,FINISHED
        }
        void send_bytes(byte[] ds)
        {

            if (serialPort1.IsOpen)
            {
                serialPort1.Write(ds, 0, ds.Length);
            }
            else
            {
                MessageBox.Show("请先连接串口");
            }
        }
        Thread rec;
        private List<ListViewItem> _out_msg;
        const int idle_tick = 3;
        bool is_ticking = false;
        void uart_check()
        {
            int last_received_timeout = 0;
            List<byte> frame = new List<byte>();
            while (true)
            {
                while (serialPort1.BytesToRead > 0)
                {
                    if (is_ticking == false)
                    {
                        is_ticking = true;
                        frame.Clear();//数据上升沿
                    }
                    frame.Add((byte)serialPort1.ReadByte());
                    last_received_timeout = 0;
                }
                if (is_ticking)
                {
                    last_received_timeout++;
                    if (last_received_timeout > idle_tick)
                    {
                        //idle callback
                        uart_deal(frame.ToArray());
                        is_ticking = false;
                    }
                }
                Thread.Sleep(10); //因为windows 调度时间片关系，太小精度意义不大
            }
        }
        void uart_deal(byte[] lb)
        {

            if(calc_check(lb,lb.Length-1)==lb[lb.Length-1])
            {
                string ss = "";
                for (int i = 0; i < lb.Length; i++)
                {
                    ss += lb[i].ToString("X2") + " ";
                }
                this.Invoke(new Action(() =>
                {
                    _out_msg.Add(new ListViewItem(new string[] { _out_msg.Count.ToString(), ss }));
                    listView1.VirtualListSize = _out_msg.Count;
                    listView1.EnsureVisible(listView1.Items.Count - 1);
                }));
                if(lb[0]==0xf0)
                {
                    if(checkBox1.Checked)
                    {
                        _fw.sending_index = 0;
                        _iapstate = IapState.SEND_INFO;
                    }
                    
                }
                else if(lb[0]==0xf1)
                {
                    if(_iapstate==IapState.WAITING)
                    {
                        _iapstate = IapState.SENDING;
                    }
                }
            }
        }

        FirmWare _fw = new FirmWare();

        private void Form1_Load(object sender, EventArgs e)
        {
            this.Text = "固件升级工具 成都合洛科技 v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string[] n = SerialPort.GetPortNames();
            foreach (string s in n)
            {
                comboBox1.Items.Add(s);
            }
            comboBox1.SelectedIndex = comboBox1.Items.Count-1;
            toolStripStatusLabel1.Text = "请先连接设备，然后加载固件";
            _out_msg = new List<ListViewItem>();
            listView1.VirtualListSize = 0;
            listView1.Columns.Add(new ColumnHeader() { Text = "ID", Width = 40 });
            listView1.Columns.Add(new ColumnHeader() { Text = "DATA" });
            listView1.Columns[1].Width = listView1.ClientSize.Width - listView1.Columns[0].Width - 30;
            new Thread(iap_loop).Start();
        }
        byte calc_check(byte[] data, int len)
        {
            int n = 0;
            for (int i = 0; i < len; i++)
            {
                n += data[i];
            }
            n = n % 256;
            return Convert.ToByte(255 - n);
        }
        byte[] _build_frame(byte cmd, byte[] data)
        {
            byte[] _tx_buf;
            if (data == null)
            {
                _tx_buf = new byte[2];
            }
            else
            {
                _tx_buf = new byte[data.Length + 2];
                data.CopyTo(_tx_buf, 1);
            }
            _tx_buf[0] = cmd;
            _tx_buf[_tx_buf.Length - 1] = calc_check(_tx_buf, _tx_buf.Length - 1);
            return _tx_buf;
        }
        bool frame_valid(byte[]v)
        {
            return calc_check(v,v.Length-1) == v[v.Length - 1];
        }
        enum IapState
        {
            IDLE,SEND_INFO,SENDING, WAITING,TIMEOUT,FINISHED
        }
        void send_info()
        {
            byte[] tx = new byte[4];
            tx[0] = _fw.block_size;
            int t = _fw.data.Length;
            tx[3] = (byte)(t&0xff);
            t = t >> 8;
            tx[2] = (byte)(t & 0xff);
            t = t >> 8;
            tx[1] = (byte)(t & 0xff);
            byte[] b = _build_frame((byte)CMD.INFO, tx);
            send_bytes(b);
        }
        void send_finished()
        {
            byte[] b = _build_frame((byte)CMD.FINISHED, null);
            send_bytes(b);
        }
        IapState _iapstate = IapState.IDLE;
        const int TICK_TIMEOUT = 1000000;
        void iap_loop()
        {
            byte []tx;
            int tick = 0;
            while(true)
            {
                switch(_iapstate)
                {
                    case IapState.IDLE:

                        break;
                    case IapState.SEND_INFO:
                        send_info();
                        _iapstate = IapState.WAITING;
                        break;
                    case IapState.SENDING:
                        if(_fw.sending_index==(_fw.data.Length/_fw.block_size))
                        {
                            int remain = _fw.data.Length - _fw.block_size * _fw.sending_index;
                            tx = new byte[remain];
                            Array.Copy(_fw.data, _fw.sending_index * _fw.block_size, tx, 0, remain);
                            _iapstate = IapState.FINISHED;
                            _fw.sending_index = 0;
                        }
                        else
                        {
                            tx = new byte[_fw.block_size];
                            Array.Copy(_fw.data, _fw.sending_index * _fw.block_size, tx, 0, _fw.block_size);
                            _iapstate = IapState.WAITING;
                        }
                        byte[] b = _build_frame(0xf1, tx);
                        send_bytes(b);
                        _fw.sending_index++;
                        tick = 0;
                        this.Invoke(new Action(() =>
                        {
                            toolStripStatusLabel1.Text = String.Format("升级中{0,4}/{1,4} 请勿断开设备电源！", _fw.sending_index, _fw.data.Length / _fw.block_size);
                        }));
                        break;
                    case IapState.WAITING:
                        tick++;
                        if (tick > TICK_TIMEOUT)
                        {
                            _iapstate = IapState.TIMEOUT;
                        }
                        break;
                    case IapState.TIMEOUT:
                        this.Invoke(new Action(() =>
                        {
                            toolStripStatusLabel1.Text = String.Format("设备无响应，请检查设备连线！");
                        }));
                        break;
                    case IapState.FINISHED:
                        Thread.Sleep(100);
                        this.Invoke(new Action(() =>
                        {
                            toolStripStatusLabel1.Text = String.Format("升级完成，请重启设备！");
                        }));
                        send_finished();
                        _iapstate = IapState.IDLE;
                        break;
                }
                Thread.Sleep(5);
            }
        }
        private void button1_Click(object sender, EventArgs e)
        {
            if (button1.Text == "连接设备")
            {
                serialPort1.PortName = comboBox1.SelectedItem.ToString();
                serialPort1.BaudRate = 115200;
                serialPort1.Encoding = Encoding.UTF8;
                serialPort1.Open();
                button1.Text = "断开设备";
                toolStripStatusLabel1.Text = "设备已连接";
                rec = new Thread(uart_check);
                rec.Start();
            }
            else
            {
                rec.Abort();
                serialPort1.Close();
                button1.Text = "连接设备";
                toolStripStatusLabel1.Text = "设备已断开";
            }
        }

        private void Button3_Click(object sender, EventArgs e)
        {

        }

        private void ComboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void ListView1_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            e.Item = _out_msg[e.ItemIndex];
        }
    }
}

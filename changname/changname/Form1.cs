using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.IO;
using System.Text.RegularExpressions;
using System.Drawing.Imaging;
using System.Globalization;
using System.Threading;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;



namespace changname
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }
        public string fy(string ystring)
        {
            string tmpf = YMLTools.GetTranslatedTextFromAPI(ystring);
            return tmpf;
        }
        private void textBox2_DragDrop(object sender, DragEventArgs e)
        {
            string path = ((System.Array)e.Data.GetData(DataFormats.FileDrop)).GetValue(0).ToString();
            //string fullPath = ((System.Array)e.Data.GetData(DataFormats.FileDrop)).GetValue(0).ToString();
            //string path = fullPath.Substring(0, fullPath.LastIndexOf("\\"));
            textBox2.Text = path;
        }

        private void textBox2_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Link; //重要代码：表明是链接类型的数据，比如文件路径
            else e.Effect = DragDropEffects.None;
        }
        public string mainpath = "";
        private void Form1_Load(object sender, EventArgs e)
        {

        }

        /// <summary>
        /// 经过优化的获取图片拍摄时间的方法。
        /// 如果成功获取，返回格式为 "yyyyMMddHHmmssfff" 的字符串，否则返回空字符串。
        /// </summary>
        /// <param name="path">图片文件的完整路径。</param>
        /// <returns>格式化后的时间字符串或空字符串。</returns>
        public string GetImageTakenTimeOptimized(string path)
        {
            if (!File.Exists(path))
            {
                return "";
            }

            DateTime? takenDate = TryGetDateFromMetadata(path);

          

            return takenDate?.ToString("yyyyMMddHHmmssfff") ?? "";
        }

        /// <summary>
        /// 1. 尝试从图片的元数据(EXIF等)中获取拍摄时间。这是最可靠的方式。
        /// </summary>
        private DateTime? TryGetDateFromMetadata(string path)
        {
            try
            {
                // 使用 FileStream 以避免锁定文件
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var directories = ImageMetadataReader.ReadMetadata(stream);

                    // 优先从 ExifSubIfdDirectory 获取，这里的标签最准确
                    var subIfdDirectory = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                    if (subIfdDirectory != null)
                    {
                        // TagDateTimeOriginal (0x9003): 原始拍摄时间，最高优先级
                        if (subIfdDirectory.TryGetDateTime(ExifSubIfdDirectory.TagDateTimeOriginal, out var date1))
                        {
                            return date1;
                        }
                        // TagDateTimeDigitized (0x9004): 数字化时间
                        if (subIfdDirectory.TryGetDateTime(ExifSubIfdDirectory.TagDateTimeDigitized, out var date2))
                        {
                            return date2;
                        }
                    }

                    // 如果上面没有，则从 ExifIfd0Directory 获取修改时间
                    var ifd0Directory = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    if (ifd0Directory != null && ifd0Directory.TryGetDateTime(ExifIfd0Directory.TagDateTime, out var date3))
                    {
                        return date3;
                    }
                }
            }
            catch (Exception ex)
            {
                // 记录日志 (可选): Console.WriteLine($"读取元数据失败: {path}, 错误: {ex.Message}");
            }
            return null;
        }

      

      






        public string changlast3(string name)
        {
            string tmp = name.Substring(0, name.Length - 3);
            Random rd = new Random(Guid.NewGuid().GetHashCode());
            tmp = tmp + rd.Next(100, 999);
            return tmp;
        }


        private void textBox2_TextChanged(object sender, EventArgs e)
        {
            mainpath = textBox2.Text;
        }

        Dictionary<string, string> dirname = new Dictionary<string, string>();

        Dictionary<string, string> chongfu = new Dictionary<string, string>();

        Dictionary<string, string> allfilename = new Dictionary<string, string>();

        public void scanfile(DirectoryInfo dir)
        {
            int indexsum = 0;
            FileInfo[] fi = dir.GetFiles();

            for (int i = 0; i < fi.Length; i++)
            {
                string strFilename = fi[i].Name;

                string strExtension = "";//后缀

                int nIndex = strFilename.LastIndexOf('.');
                if (nIndex >= 0)
                {
                    strExtension = strFilename.Substring(nIndex);
                }

                if (strExtension.ToLower() == ".jpg")
                {
                    indexsum++;
                    allfilename.Add(fi[i].FullName, indexsum.ToString());
                }
                else
                {
                    if (!System.IO.Directory.Exists(fi[i].DirectoryName + "\\other"))
                    {
                        System.IO.Directory.CreateDirectory(fi[i].DirectoryName + "\\other");
                    }


                    string newname = "V" + fi[i].LastWriteTime.ToString("yyyyMMddHHmmssfff");

                    while (true)
                    {
                        if (File.Exists(fi[i].DirectoryName + "\\other\\" + newname + strExtension.ToLower()))
                        {
                            newname = changlast3(newname);
                            //修改文件名
                        }
                        else
                        {
                            break;
                        }

                    }



                    //判断目录文件是否存在 
                    switch (strExtension.ToLower())
                    {
                        case ".mp4": File.Move(fi[i].FullName, fi[i].DirectoryName + "\\other\\" + newname + ".mp4"); break;
                        case ".mov": File.Move(fi[i].FullName, fi[i].DirectoryName + "\\other\\" + newname + ".mov"); break;
                        default: File.Move(fi[i].FullName, fi[i].DirectoryName + "\\other\\" + fi[i].Name); break;
                    }

                    //移动到其他文件夹
                }
            }
            this.BeginInvoke(new MethodInvoker(() =>
            {
                try
                {

                    label3.Text = "提示：找到" + allfilename.Count + "个文件";
                    label4.Text = "提示：当前处理目录" + dir.FullName;
                }
                catch { }
            }));

        }
        /// <summary>
        /// 获取图片的设备名称（相机/手机品牌+型号），如无则返回 UnknownDevice。
        /// </summary>
        public string GetDeviceNameFromImage(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var directories = ImageMetadataReader.ReadMetadata(stream);
                    var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    if (ifd0 != null)
                    {
                        string make = ifd0.GetDescription(ExifIfd0Directory.TagMake) ?? "";
                        string model = ifd0.GetDescription(ExifIfd0Directory.TagModel) ?? "";
                        string device = (make + "_" + model).Trim('_').Replace(' ', '_');
                        if (!string.IsNullOrWhiteSpace(device) && device != "_")
                            return device;
                    }
                }
            }
            catch { }
            return "UnknownDevice";
        }
        public void dochang()
        {

            string tmpnow = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            int wc = 0;
            foreach (var item in allfilename.Keys)
            {
                FileInfo fi = new FileInfo(item);

                if (((fi.Name[0] == '2' && fi.Name.Length == 21) || (fi.Name[1] == '2' && fi.Name[0] == 'X' && fi.Name.Length == 22)) && (checkBox2.Checked))
                {
                    //无需处理
                }
                else
                {

                    Microsoft.VisualBasic.FileIO.FileSystem.RenameFile(fi.FullName, tmpnow + allfilename[item] + ".jpg");
                    //File.Move(fi.FullName, fi.Directory.FullName + "\\" +tmpnow + allfilename[item] + ".jpg" );//先改顺序名
                    fi = new FileInfo(fi.Directory.FullName + "\\" + tmpnow + allfilename[item] + ".jpg");

                    string deviceName = GetDeviceNameFromImage(fi.FullName);
                    //string deviceDir = Path.Combine(fi.Directory.FullName, deviceName);
                    //if (!System.IO.Directory.Exists(deviceDir))
                    //    System.IO.Directory.CreateDirectory(deviceDir);

                    string newname = "X" + fi.LastWriteTime.ToString("yyyyMMddHHmmssfff");

                    string shtime = GetImageTakenTimeOptimized(fi.FullName);//得到拍摄时间

                    if (shtime != "")
                    {
                        newname = shtime;
                    }

                    string baseName = newname;
                    int counter = 1;
                    // 循环直到找到一个在本次运行中未使用过，且目标文件夹中也不存在的文件名
                    while (chongfu.ContainsKey(newname) )
                    {
                        newname = $"{baseName}_{counter++}";
                    }

                    chongfu.Add(newname, newname);

                    try
                    {
                        // 移动文件到设备对应的文件夹，并使用最终确定的唯一文件名
                        File.Move(fi.FullName, fi.Directory.FullName + "\\"+ deviceName +"_"+ newname + ".jpg");
                    }
                    catch (Exception ex)
                    {
                        // 捕获并显示错误，防止程序卡死
                        this.BeginInvoke(new MethodInvoker(() =>
                        {
                            label4.Text = $"处理文件 {fi.Name} 出错: {ex.Message}";
                        }));
                    }
                }
                wc++;
                this.BeginInvoke(new MethodInvoker(() =>
                {
                    try
                    {
                        label3.Text = "提示：修改" + wc + "个文件" + "，文件总数：" + allfilename.Count;
                        this.progressBar1.Value = (int)(((wc + 1) / (double)allfilename.Count) * 100);
                        label2.Text = this.progressBar1.Value + "%";

                    }
                    catch { }
                }));

            }
            this.BeginInvoke(new MethodInvoker(() =>
            {
                try
                {

                    this.progressBar1.Value = 100;
                    label2.Text = "100%";
                    label3.Text = "提示：全部完成！";
                    label4.Text = "";
                }
                catch { }
            }));

        }

        private void button1_Click(object sender, EventArgs e)
        {

            allfilename = new Dictionary<string, string>();
            chongfu = new Dictionary<string, string>();
            DirectoryInfo dir = new DirectoryInfo(mainpath);



            #region 扫描
            System.Threading.Thread doinseruser = new System.Threading.Thread(delegate ()
            {
                try
                {
                    if (checkBox1.Checked)
                    {
                        //获取子目录
                        DirectoryInfo[] dirs = dir.GetDirectories();
                        foreach (var item in dirs)
                        {
                            int year = 0;
                            try
                            {
                                year = int.Parse(item.Name);
                            }
                            catch { }

                            if (year > 1900 && year < 3000)
                            {
                                allfilename = new Dictionary<string, string>();
                                chongfu = new Dictionary<string, string>();
                                scanfile(item);//递归找出所有jpg文件 和修改目标
                                               //开始处理文件
                                dochang();
                            }

                        }


                    }
                    else
                    {
                        scanfile(dir);//递归找出所有jpg文件 和修改目标
                                      //开始处理文件
                        dochang();
                    }




                }
                catch
                {
                }
            });
            doinseruser.IsBackground = true;
            doinseruser.Start();
            #endregion


            //进行文件名修改
        }
        string newdotname = "";
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            //   newdotname = "";
        }

        private void button2_Click(object sender, EventArgs e)
        {
            string tmpasda = changlast3("20150505171717555");
            //string fystr = fy("AllianceHelpRequestItem");
        }
    }
}

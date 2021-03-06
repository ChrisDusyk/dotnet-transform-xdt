using System;
using System.Text;
using System.Xml;
using System.IO;

namespace Microsoft.DotNet.Xdt.Tools
{
    public class XmlFileInfoDocument : XmlDocument, IDisposable
    {
        private Encoding _textEncoding;
        private XmlReader _reader;

        private int _lineNumberOffset;
        private int _linePositionOffset;

        public override void Load(XmlReader reader)
        {
            if (_reader != null)
            {
                FileName = _reader.BaseURI;
            }

            base.Load(reader);
            PreserveWhitespace = true;

            FirstLoad = false;
        }

        private void LoadFromFileName(string filename)
        {
            FileName = filename;

            StreamReader reader = null;
            try
            {
                if (PreserveWhitespace)
                {
                    PreservationProvider = new XmlAttributePreservationProvider(filename);
                }

                using (var stream = File.OpenText(filename))
                {
                    LoadFromTextReader(reader, reader.CurrentEncoding);
                }
            }
            finally
            {
                if (PreservationProvider != null)
                {
                    PreservationProvider.Close();
                    PreservationProvider = null;
                }
                reader?.Dispose();
            }
        }

        private void LoadFromTextReader(TextReader textReader, Encoding encoding)
        {
            var streamReader = textReader as StreamReader;
            if (streamReader != null)
            {
                var fileStream = streamReader.BaseStream as FileStream;
                if (fileStream != null)
                {
                    FileName = fileStream.Name;
                }

                _textEncoding = GetEncodingFromStream(streamReader.BaseStream);
            }

            _reader = XmlReader.Create(textReader, new XmlReaderSettings());

            base.Load(_reader);

            if (_textEncoding == null)
            {
                _textEncoding = encoding;
            }
        }

        private static Encoding GetEncodingFromStream(Stream stream)
        {
            Encoding encoding = null;
            if (stream.CanSeek)
            {
                var buffer = new byte[3];
                stream.Read(buffer, 0, buffer.Length);

                if (buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    encoding = Encoding.UTF8;
                else if (buffer[0] == 0xFE && buffer[1] == 0xFF)
                    encoding = Encoding.BigEndianUnicode;
                else if (buffer[0] == 0xFF && buffer[1] == 0xFE)
                    encoding = Encoding.Unicode;
                else if (buffer[0] == 0x2B && buffer[1] == 0x2F && buffer[2] == 0x76)
                    encoding = Encoding.UTF7;

                // Reset the stream
                stream.Seek(0, SeekOrigin.Begin);
            }

            return encoding;
        }

        internal XmlNode CloneNodeFromOtherDocument(XmlNode element)
        {
            XmlReader oldReader = _reader;
            string oldFileName = FileName;

            XmlNode clone;
            try
            {
                var lineInfo = element as IXmlLineInfo;
                if (lineInfo != null)
                {
                    _reader = XmlReader.Create(new StringReader(element.OuterXml));

                    _lineNumberOffset = lineInfo.LineNumber - 1;
                    _linePositionOffset = lineInfo.LinePosition - 2;
                    FileName = element.OwnerDocument.BaseURI;

                    clone = ReadNode(_reader);
                }
                else
                {
                    FileName = null;
                    _reader = null;

                    clone = ReadNode(XmlReader.Create(new StringReader(element.OuterXml)));
                }
            }
            finally
            {
                _lineNumberOffset = 0;
                _linePositionOffset = 0;
                FileName = oldFileName;

                _reader = oldReader;
            }

            return clone;
        }

        internal bool HasErrorInfo => _reader != null;

        internal string FileName { get; private set; }

        private int CurrentLineNumber => (_reader as IXmlLineInfo)?.LineNumber + _lineNumberOffset ?? 0;

        private int CurrentLinePosition => (_reader as IXmlLineInfo)?.LinePosition + _linePositionOffset ?? 0;

        private bool FirstLoad { get; set; } = true;

        private XmlAttributePreservationProvider PreservationProvider { get; set; }

        private Encoding TextEncoding
        {
            get
            {
                if (_textEncoding != null)
                {
                    return _textEncoding;
                }
                // Copied from base implementation of XmlDocument
                if (HasChildNodes)
                {
                    XmlDeclaration declaration = FirstChild as XmlDeclaration;
                string value = declaration?.Encoding;
                    if (value?.Length > 0)
                    {
                        return Encoding.GetEncoding(value);
                    }
                }
                return null;
            }
        }

        public override void Save(TextWriter writer)
        {
            XmlWriter xmlWriter = null;
            try
            {
                if (PreserveWhitespace)
                {
                    XmlFormatter.Format(this);
                    xmlWriter = new XmlAttributePreservingWriter(writer);
                }
                else
                {
                    xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings
                    {
                        Encoding = TextEncoding,
                        Indent = true,
                    });
                }
                WriteTo(xmlWriter);
            }
            finally
            {
                if (xmlWriter != null)
                {
                    xmlWriter.Flush();
                    xmlWriter.Dispose();
                }
            }
        }

        public override void Save(Stream w)
        {
            XmlWriter xmlWriter = null;
            try
            {
                if (PreserveWhitespace)
                {
                    XmlFormatter.Format(this);
                    xmlWriter = new XmlAttributePreservingWriter(w, TextEncoding);
                }
                else
                {
                    xmlWriter = XmlWriter.Create(w, new XmlWriterSettings
                    {
                        Encoding = TextEncoding,
                        Indent = true,
                    });
                }
                WriteTo(xmlWriter);
            }
            finally
            {
                xmlWriter?.Flush();
            }
        }

        public override XmlElement CreateElement(string prefix, string localName, string namespaceUri)
        {
            return HasErrorInfo ? new XmlFileInfoElement(prefix, localName, namespaceUri, this) : base.CreateElement(prefix, localName, namespaceUri);
        }

        public override XmlAttribute CreateAttribute(string prefix, string localName, string namespaceUri)
        {
            return HasErrorInfo ? new XmlFileInfoAttribute(prefix, localName, namespaceUri, this) : base.CreateAttribute(prefix, localName, namespaceUri);
        }

        internal bool IsNewNode(XmlNode node)
        {
            // The transformation engine will only add elements. Anything
            // else that gets added must be contained by a new element.
            // So to determine what's new, we search up the tree for a new
            // element that contains this node.
            var element = FindContainingElement(node) as XmlFileInfoElement;
            return element != null && !element.IsOriginal;
        }

        private XmlElement FindContainingElement(XmlNode node)
        {
            while (node != null && !(node is XmlElement))
            {
                node = node.ParentNode;
            }
            return node as XmlElement;
        }

        private class XmlFileInfoElement : XmlElement, IXmlLineInfo, IXmlFormattableAttributes
        {
            private readonly XmlAttributePreservationDict _preservationDict;

            internal XmlFileInfoElement(string prefix, string localName, string namespaceUri, XmlFileInfoDocument document)
                : base(prefix, localName, namespaceUri, document)
            {
                LineNumber = document.CurrentLineNumber;
                LinePosition = document.CurrentLinePosition;
                IsOriginal = document.FirstLoad;

                if (document.PreservationProvider != null)
                {
                    _preservationDict = document.PreservationProvider.GetDictAtPosition(LineNumber, LinePosition - 1);
                }
                if (_preservationDict == null)
                {
                    _preservationDict = new XmlAttributePreservationDict();
                }
            }

            public override void WriteTo(XmlWriter w)
            {
                string prefix = Prefix;
                if (!string.IsNullOrEmpty(NamespaceURI))
                {
                    prefix = w.LookupPrefix(NamespaceURI) ?? Prefix;
                }

                w.WriteStartElement(prefix, LocalName, NamespaceURI);

                if (HasAttributes)
                {
                    var preservingWriter = w as XmlAttributePreservingWriter;
                    if (preservingWriter == null || _preservationDict == null)
                    {
                        WriteAttributesTo(w);
                    }
                    else
                    {
                        WritePreservedAttributesTo(preservingWriter);
                    }
                }

                if (IsEmpty)
                {
                    w.WriteEndElement();
                }
                else
                {
                    WriteContentTo(w);
                    w.WriteFullEndElement();
                }
            }

            private void WriteAttributesTo(XmlWriter w)
            {
                XmlAttributeCollection attrs = Attributes;
                for (var i = 0; i < attrs.Count; i += 1)
                {
                    XmlAttribute attr = attrs[i];
                    attr.WriteTo(w);
                }
            }

            private void WritePreservedAttributesTo(XmlAttributePreservingWriter preservingWriter) 
            {
                _preservationDict.WritePreservedAttributes(preservingWriter, Attributes);
            }

            public bool HasLineInfo()
            {
                return true;
            }

            public int LineNumber { get; }

            public int LinePosition { get; }

            public bool IsOriginal { get; }

            void IXmlFormattableAttributes.FormatAttributes(XmlFormatter formatter) => _preservationDict.UpdatePreservationInfo(Attributes, formatter);

            string IXmlFormattableAttributes.AttributeIndent => _preservationDict.GetAttributeNewLineString(null);
        }

        private class XmlFileInfoAttribute : XmlAttribute, IXmlLineInfo
        {
            internal XmlFileInfoAttribute(string prefix, string localName, string namespaceUri, XmlFileInfoDocument document)
                : base(prefix, localName, namespaceUri, document)
            {
                LineNumber = document.CurrentLineNumber;
                LinePosition = document.CurrentLinePosition;
            }

            public bool HasLineInfo()
            {
                return true;
            }

            public int LineNumber { get; }

            public int LinePosition { get; }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }

            if (PreservationProvider != null)
            {
                PreservationProvider.Close();
                PreservationProvider = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~XmlFileInfoDocument()
        {
            //Debug.Fail("call dispose please");
            Dispose(false);
        }
    }
}
